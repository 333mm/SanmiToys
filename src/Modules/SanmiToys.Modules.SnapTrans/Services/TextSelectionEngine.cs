using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using SanmiToys.Core.Helpers;
using SanmiToys.Modules.SnapTrans.Models;
using SanmiToys.Modules.SnapTrans.Views;

namespace SanmiToys.Modules.SnapTrans.Services;

public class TextSelectionEngine : IDisposable
{
    private readonly Func<SnapTransSettings> _settingsAccessor;
    private readonly TranslationService _translationService;
    private readonly TextToSpeechService _ttsService;

    private IntPtr _mouseHookId = IntPtr.Zero;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;

    private bool _isMouseDown;
    private NativeMethods.POINT _startPt;
    private long _lastClickTime;
    private NativeMethods.POINT _lastClickPt;
    private int _clickCount;
    private SelectionMiniToolbar? _toolbar;

    public bool IsRunning => _mouseHookId != IntPtr.Zero;

    public TextSelectionEngine(
        Func<SnapTransSettings> settingsAccessor,
        TranslationService translationService,
        TextToSpeechService ttsService)
    {
        _settingsAccessor = settingsAccessor;
        _translationService = translationService;
        _ttsService = ttsService;
    }

    public void Start()
    {
        if (_mouseHookId != IntPtr.Zero) return;

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var hModule = curModule != null ? NativeMethods.GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

        _mouseHookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hModule, 0);
        _keyboardHookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, hModule, 0);
    }

    public void Stop()
    {
        if (_mouseHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
            _mouseProc = null;
        }

        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
            _keyboardProc = null;
        }

        CloseCurrentToolbar();
        _isMouseDown = false;
        _clickCount = 0;
    }

    private void EnsureToolbarCreated()
    {
        if (_toolbar == null)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    var settings = _settingsAccessor();
                    _toolbar = new SelectionMiniToolbar(settings, _translationService, _ttsService);
                }
                else
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        var settings = _settingsAccessor();
                        _toolbar = new SelectionMiniToolbar(settings, _translationService, _ttsService);
                    });
                }
            }
        }
    }

    private void CloseCurrentToolbar()
    {
        if (_toolbar != null)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
                {
                    app.Dispatcher.InvokeAsync(() =>
                    {
                        _toolbar?.HideToolbar();
                    });
                }
            }
            catch { }
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var settings = _settingsAccessor();
            if (settings.IsEnabled && settings.EnableSelectionToolbar)
            {
                int msg = wParam.ToInt32();
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                switch (msg)
                {
                    case NativeMethods.WM_LBUTTONDOWN:
                        HandleMouseDown(hookStruct.pt);
                        break;

                    case NativeMethods.WM_LBUTTONUP:
                        HandleMouseUp(hookStruct.pt, settings);
                        break;

                    case NativeMethods.WM_RBUTTONDOWN:
                    case NativeMethods.WM_MBUTTONDOWN:
                        HandleOtherClick();
                        break;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var settings = _settingsAccessor();
            if (settings.IsEnabled && settings.EnableSelectionToolbar)
            {
                int msg = wParam.ToInt32();
                if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
                {
                    var kbdStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    if (IsMatchingModifierKey(kbdStruct.vkCode, settings.SelectionToolbarModifier))
                    {
                        // マウスドラッグ中ではない場合、テキスト選択後に修飾キーを押したと判定してツールバー表示
                        if (!_isMouseDown)
                        {
                            NativeMethods.GetCursorPos(out var pt);
                            _ = Task.Run(() => CaptureAndShowToolbarAsync(pt, settings));
                        }
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private static bool IsMatchingModifierKey(uint vkCode, string modifier)
    {
        return modifier switch
        {
            "Ctrl" => vkCode is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL,
            "Alt" => vkCode is NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU,
            "Shift" => vkCode is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT,
            _ => false
        };
    }

    private void HandleMouseDown(NativeMethods.POINT pt)
    {
        _startPt = pt;
        _isMouseDown = true;

        long now = Environment.TickCount64;
        int doubleClickTime = System.Windows.Forms.SystemInformation.DoubleClickTime;
        var doubleClickSize = System.Windows.Forms.SystemInformation.DoubleClickSize;

        long elapsed = now - _lastClickTime;
        int diffX = Math.Abs(pt.X - _lastClickPt.X);
        int diffY = Math.Abs(pt.Y - _lastClickPt.Y);

        if (elapsed <= doubleClickTime && diffX <= doubleClickSize.Width && diffY <= doubleClickSize.Height)
        {
            _clickCount++;
        }
        else
        {
            _clickCount = 1;
        }

        _lastClickTime = now;
        _lastClickPt = pt;

        // 既存のミニツールバーが表示されており、クリック位置がツールバー外なら隠す
        if (_toolbar != null && _toolbar.IsVisible)
        {
            var app = System.Windows.Application.Current;
            app?.Dispatcher.InvokeAsync(() =>
            {
                if (_toolbar != null && _toolbar.IsVisible && !_toolbar.ContainsScreenPoint(pt.X, pt.Y))
                {
                    _toolbar.HideToolbar();
                }
            });
        }
    }

    private void HandleOtherClick()
    {
        _isMouseDown = false;
        _clickCount = 0;
        if (_toolbar != null)
        {
            CloseCurrentToolbar();
        }
    }

    private void HandleMouseUp(NativeMethods.POINT pt, SnapTransSettings settings)
    {
        if (!_isMouseDown) return;
        _isMouseDown = false;

        // ツールバー自身の上でのマウスアップは除外
        if (_toolbar != null && _toolbar.IsVisible && _toolbar.ContainsScreenPoint(pt.X, pt.Y))
        {
            return;
        }

        // ドラッグ移動量の判定（8ピクセル以上のドラッグで選択とみなす）
        int dx = pt.X - _startPt.X;
        int dy = pt.Y - _startPt.Y;
        int distSq = (dx * dx) + (dy * dy);
        bool isDragSelection = distSq >= 64; // 8^2 = 64
        bool isMultiClickSelection = _clickCount >= 2; // ダブルクリックまたはトリプルクリック

        if (isDragSelection)
        {
            _clickCount = 0;
        }

        if (!isDragSelection && !isMultiClickSelection)
        {
            return;
        }

        // 修飾キーの判定
        if (!CheckModifier(settings.SelectionToolbarModifier))
        {
            return;
        }

        // まずUI Automation（精度100%・非破壊）を試行し、非対応アプリでは修飾キー付きフォールバックコピーを実行
        _ = Task.Run(() => CaptureAndShowToolbarAsync(pt, settings));
    }

    private static bool CheckModifier(string modifier)
    {
        return modifier switch
        {
            "Ctrl" => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0,
            "Alt" => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0,
            "Shift" => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0,
            _ => true // "None" またはその他は常に許可
        };
    }

    public async Task<bool> TryTriggerToolbarNearCursorAsync()
    {
        NativeMethods.GetCursorPos(out var pt);
        string? selectedText = await GetSelectedTextAsync(pt).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                await app.Dispatcher.InvokeAsync(() =>
                {
                    EnsureToolbarCreated();
                    _toolbar?.ShowAt(selectedText, pt.X, pt.Y);
                });
                return true;
            }
        }
        return false;
    }

    private async Task CaptureAndShowToolbarAsync(NativeMethods.POINT pt, SnapTransSettings settings)
    {
        // アプリケーション側が選択範囲を描画・確定するのを微小待機
        await Task.Delay(40).ConfigureAwait(false);

        string? selectedText = await GetSelectedTextAsync(pt).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                await app.Dispatcher.InvokeAsync(() =>
                {
                    EnsureToolbarCreated();
                    _toolbar?.ShowAt(selectedText, pt.X, pt.Y);
                });
            }
        }
    }

    public async Task<string?> GetSelectedTextAsync(NativeMethods.POINT pt)
    {
        // 1. UI Automation による完全非破壊・高速・精度100%の直接取得（ブラウザやOffice、メモ帳等）
        string? text = GetSelectedTextFromUiAutomation(pt);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        // 2. UI Automation 非対応アプリ（マインクラフト等のゲーム、Java、カスタムUI等）向け安全なフォールバック
        return await GetSelectedTextViaClipboardFallbackAsync().ConfigureAwait(false);
    }

    private async Task<string?> GetSelectedTextViaClipboardFallbackAsync()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null || app.Dispatcher.HasShutdownStarted) return null;

        // クリップボードの退避（STAスレッドで実行）
        System.Windows.IDataObject? originalData = null;
        try
        {
            app.Dispatcher.Invoke(() =>
            {
                try
                {
                    originalData = System.Windows.Clipboard.GetDataObject();
                }
                catch { }
            });
        }
        catch { }

        uint initialSeq = NativeMethods.GetClipboardSequenceNumber();

        // 物理的に押下中の修飾キー（特に Alt や Shift）の干渉を防ぐため解放
        ReleaseModifierKeys();

        // Ctrl + C 送信
        SendCtrlC();

        // クリップボードの更新を最大 150ms 待機 (15ms 間隔ポーリング)
        string? copiedText = null;
        bool clipboardChanged = false;

        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(15).ConfigureAwait(false);
            uint currentSeq = NativeMethods.GetClipboardSequenceNumber();
            if (currentSeq != initialSeq)
            {
                clipboardChanged = true;
                break;
            }
        }

        if (clipboardChanged)
        {
            try
            {
                app.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (System.Windows.Clipboard.ContainsText())
                        {
                            copiedText = System.Windows.Clipboard.GetText();
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        // 元のクリップボード内容を復元（ユーザーの大切なクリップボード履歴を保護）
        if (clipboardChanged && originalData != null)
        {
            try
            {
                app.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        System.Windows.Clipboard.SetDataObject(originalData, true);
                    }
                    catch { }
                });
            }
            catch { }
        }

        return !string.IsNullOrWhiteSpace(copiedText) ? copiedText.Trim() : null;
    }

    private static void ReleaseModifierKeys()
    {
        var inputs = new List<NativeMethods.INPUT>();

        if ((NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_MENU, true));
        }
        if ((NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_SHIFT, true));
        }

        if (inputs.Count > 0)
        {
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
        }
    }

    private static void SendCtrlC()
    {
        var inputs = new NativeMethods.INPUT[4];
        inputs[0] = CreateKeyInput((ushort)NativeMethods.VK_CONTROL, false); // Ctrl down
        inputs[1] = CreateKeyInput((ushort)NativeMethods.VK_C, false);       // C down
        inputs[2] = CreateKeyInput((ushort)NativeMethods.VK_C, true);        // C up
        inputs[3] = CreateKeyInput((ushort)NativeMethods.VK_CONTROL, true);  // Ctrl up

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT CreateKeyInput(ushort vk, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static string? GetSelectedTextFromUiAutomation(NativeMethods.POINT pt)
    {
        string? text = null;

        // 1. フォーカス要素から選択テキストを取得（最優先・最高速）
        try
        {
            var focusedElement = AutomationElement.FocusedElement;
            text = ExtractSelectedText(focusedElement);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        catch { }

        // 2. マウス座標の要素から取得（Webブラウザやマルチペインアプリ等）
        try
        {
            var elementFromPoint = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            text = ExtractSelectedText(elementFromPoint);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        catch { }

        return null;
    }

    private static string? ExtractSelectedText(AutomationElement? element)
    {
        if (element == null) return null;

        var current = element;
        int depth = 0;
        while (current != null && depth < 6)
        {
            try
            {
                if (current.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObj) &&
                    patternObj is TextPattern textPattern)
                {
                    var selectionRanges = textPattern.GetSelection();
                    if (selectionRanges != null && selectionRanges.Length > 0)
                    {
                        string text = selectionRanges[0].GetText(-1);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }
            }
            catch { }

            try
            {
                current = TreeWalker.ControlViewWalker.GetParent(current);
                depth++;
            }
            catch
            {
                break;
            }
        }

        return null;
    }

    public void Dispose()
    {
        Stop();
        if (_toolbar != null)
        {
            try
            {
                var app = System.Windows.Application.Current;
                app?.Dispatcher.InvokeAsync(() =>
                {
                    _toolbar?.Close();
                    _toolbar = null;
                });
            }
            catch { }
        }
        GC.SuppressFinalize(this);
    }
}
