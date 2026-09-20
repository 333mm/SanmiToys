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
    private bool _isModifierPressed;
    private int _isCapturing;
    private long _lastCaptureTime;
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
        _isModifierPressed = false;
        _isCapturing = 0;
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
                        // 長押しによるオートリピート（毎秒30回以上の連続発火）を完全抑止
                        if (_isModifierPressed)
                        {
                            // フェイルセーフ: 物理的にキーが離されている場合は状態を復旧
                            if ((NativeMethods.GetKeyState((int)kbdStruct.vkCode) & 0x8000) == 0)
                            {
                                _isModifierPressed = false;
                            }
                            else
                            {
                                // オートリピート中なので追加タスクを生成せず即座に抜ける
                                return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
                            }
                        }

                        _isModifierPressed = true;

                        // マウスドラッグ中ではない場合、テキスト選択後に修飾キーを押したと判定してツールバー表示
                        if (!_isMouseDown)
                        {
                            NativeMethods.GetCursorPos(out var pt);
                            _ = Task.Run(() => CaptureAndShowToolbarAsync(pt, settings));
                        }
                    }
                }
                else if (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP)
                {
                    var kbdStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    if (IsMatchingModifierKey(kbdStruct.vkCode, settings.SelectionToolbarModifier))
                    {
                        _isModifierPressed = false;
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
        // 多重実行排他制御: 前回のキャプチャ処理が完了するまで重複起動を完全に遮断
        if (System.Threading.Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // スロットリング: 最低 200ms の間隔を空けて UI スレッドの過負荷を防止
            long now = Environment.TickCount64;
            if (now - _lastCaptureTime < 200)
            {
                return;
            }
            _lastCaptureTime = now;

            // ツールバーが既に表示中の場合は重複キャプチャを防止
            if (_toolbar?.IsShowing == true)
            {
                return;
            }

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
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isCapturing, 0);
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

        // 物理押下中の修飾キー（Alt等）を保護しながら安全に Ctrl + C を送信
        SendCopyCommandSafely();

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

    /// <summary>
    /// クリップボードコピー用のキーストローク入力をアトミックに構築します。
    /// 物理的に Alt や Shift が押下されている場合でも、Ctrl を先行押下した状態で Alt を一時解放し、
    /// C を送出した直後に Alt を物理押下状態に戻すことで、メニューバーの誤起動を完全に防ぎつつ
    /// ユーザーの物理キー状態を整合させます。
    /// </summary>
    public static List<NativeMethods.INPUT> BuildCopyCommandInputs(bool wasCtrlDown, bool wasAltDown, bool wasShiftDown)
    {
        var inputs = new List<NativeMethods.INPUT>();

        // 1. まず Ctrl を押下（未押下の場合）
        if (!wasCtrlDown)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_CONTROL, false));
        }

        // 2. 物理的に押下されている Alt や Shift を一時解放
        // (Ctrlが押下された状態で行うため、Alt単独押し扱いにならずメニューバーの誤起動を防ぐ)
        if (wasAltDown)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_MENU, true));
        }
        if (wasShiftDown)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_SHIFT, true));
        }

        // 3. C を押下・解放して純粋な Ctrl + C を送信
        inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_C, false));
        inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_C, true));

        // 4. 一時解放した修飾キーを元の押下状態に復帰（ユーザーの物理ホールド状態を保護）
        if (wasAltDown)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_MENU, false));
        }
        if (wasShiftDown)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_SHIFT, false));
        }

        // 5. Ctrl を解放（元々物理的に押下されていない場合のみ解放）
        if (!wasCtrlDown)
        {
            inputs.Add(CreateKeyInput((ushort)NativeMethods.VK_CONTROL, true));
        }

        return inputs;
    }

    private static void SendCopyCommandSafely()
    {
        bool wasCtrlDown = (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        bool wasAltDown = (NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        bool wasShiftDown = (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

        var inputs = BuildCopyCommandInputs(wasCtrlDown, wasAltDown, wasShiftDown);
        if (inputs.Count > 0)
        {
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
        }
    }

    public static NativeMethods.INPUT CreateKeyInput(ushort vk, bool keyUp)
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
