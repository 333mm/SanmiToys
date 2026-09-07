using System;
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

    private IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _proc;

    private bool _isMouseDown;
    private NativeMethods.POINT _startPt;
    private long _lastClickTime;
    private NativeMethods.POINT _lastClickPt;
    private int _clickCount;
    private SelectionMiniToolbar? _toolbar;

    public bool IsRunning => _hookId != IntPtr.Zero;

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
        if (_hookId != IntPtr.Zero) return;

        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var hModule = curModule != null ? NativeMethods.GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, hModule, 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _proc = null;
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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
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

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
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
        CloseCurrentToolbar();
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

        // UI Automation を使って完全非同期で選択テキストを直接取得（Ctrl+Cやクリップボード操作は完全ゼロ）
        _ = Task.Run(() => CaptureAndShowToolbarViaUiaAsync(pt));
    }

    private static bool CheckModifier(string modifier)
    {
        return modifier switch
        {
            "Ctrl" => (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0,
            "Alt" => (NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0,
            "Shift" => (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0,
            _ => true // "None" またはその他は常に許可
        };
    }

    private async Task CaptureAndShowToolbarViaUiaAsync(NativeMethods.POINT pt)
    {
        // アプリケーション側が選択範囲を確定するのを微小待機
        await Task.Delay(40).ConfigureAwait(false);

        string? selectedText = GetSelectedTextFromUiAutomation(pt);

        // 1回目で取得できなかった場合、わずかに待機して再試行
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            await Task.Delay(60).ConfigureAwait(false);
            selectedText = GetSelectedTextFromUiAutomation(pt);
        }

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

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObj) &&
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
