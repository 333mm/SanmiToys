using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
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
    private SelectionMiniToolbar? _currentToolbar;

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

    private void CloseCurrentToolbar()
    {
        if (_currentToolbar != null)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
                {
                    app.Dispatcher.InvokeAsync(() =>
                    {
                        _currentToolbar?.Close();
                        _currentToolbar = null;
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

        // 既存のミニツールバーが表示されており、クリック位置がツールバー外なら閉じる
        if (_currentToolbar != null)
        {
            var app = System.Windows.Application.Current;
            app?.Dispatcher.InvokeAsync(() =>
            {
                if (_currentToolbar != null && !_currentToolbar.ContainsScreenPoint(pt.X, pt.Y))
                {
                    _currentToolbar.Close();
                    _currentToolbar = null;
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
        if (_currentToolbar != null && _currentToolbar.ContainsScreenPoint(pt.X, pt.Y))
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

        // フックをブロックしないよう非同期でテキスト取得およびツールバー表示を実行
        _ = Task.Run(() => CaptureAndShowToolbarAsync(pt));
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

    private async Task CaptureAndShowToolbarAsync(NativeMethods.POINT pt)
    {
        // アプリケーション側がテキスト選択を確定するのを待機
        await Task.Delay(80);

        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null || app.Dispatcher.HasShutdownStarted) return;

        await app.Dispatcher.InvokeAsync(async () =>
        {
            string? selectedText = null;
            System.Windows.IDataObject? oldClipboard = null;

            try
            {
                // 1. 直前のクリップボード内容を一時退避
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        oldClipboard = System.Windows.Clipboard.GetDataObject();
                    }
                }
                catch { }

                // 2. 一旦クリア
                try { System.Windows.Clipboard.Clear(); } catch { }

                // 3. Ctrl + C を送信
                NativeMethods.SendCtrlC();

                // 4. クリップボードへの反映を待機
                for (int i = 0; i < 6; i++)
                {
                    await Task.Delay(40);
                    try
                    {
                        if (System.Windows.Clipboard.ContainsText())
                        {
                            var text = System.Windows.Clipboard.GetText();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                selectedText = text.Trim();
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                // 5. 元のクリップボード内容を即座に復元
                try
                {
                    if (oldClipboard != null)
                    {
                        System.Windows.Clipboard.SetDataObject(oldClipboard, true);
                    }
                    else
                    {
                        System.Windows.Clipboard.Clear();
                    }
                }
                catch { }
            }

            // 6. 有効なテキストが取得できた場合にミニツールバーを表示
            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                _currentToolbar?.Close();

                var currentSettings = _settingsAccessor();
                var toolbar = new SelectionMiniToolbar(selectedText, currentSettings, _translationService, _ttsService);
                toolbar.SetPosition(pt.X, pt.Y);
                toolbar.Closed += (s, e) =>
                {
                    if (_currentToolbar == toolbar)
                    {
                        _currentToolbar = null;
                    }
                };

                _currentToolbar = toolbar;
                toolbar.Show();
            }
        });
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
