using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SanmiToys.Modules.FluidDrag.Models;

namespace SanmiToys.Modules.FluidDrag.Core;

public class WindowDragEngine : IDisposable
{
    private readonly Func<FluidDragSettings> _settingsAccessor;
    private IntPtr _hookId = IntPtr.Zero;
    private FluidDragNativeMethods.LowLevelMouseProc? _proc;

    private bool _isPendingDrag;
    private bool _isDragging;
    private FluidDragNativeMethods.POINT _startMousePoint;
    private FluidDragNativeMethods.RECT _startWindowRect;
    private IntPtr _targetHwnd = IntPtr.Zero;

    public bool IsRunning => _hookId != IntPtr.Zero;

    public WindowDragEngine(Func<FluidDragSettings> settingsAccessor)
    {
        _settingsAccessor = settingsAccessor;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var hModule = curModule != null ? FluidDragNativeMethods.GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

        _hookId = FluidDragNativeMethods.SetWindowsHookEx(FluidDragNativeMethods.WH_MOUSE_LL, _proc, hModule, 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            FluidDragNativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _proc = null;
            ResetDragState();
        }
    }

    private void ResetDragState()
    {
        _isPendingDrag = false;
        _isDragging = false;
        _targetHwnd = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var settings = _settingsAccessor();
            if (!settings.IsEnabled)
            {
                ResetDragState();
                return FluidDragNativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            int msg = wParam.ToInt32();
            var hookStruct = Marshal.PtrToStructure<FluidDragNativeMethods.MSLLHOOKSTRUCT>(lParam);
            var pt = hookStruct.pt;

            switch (msg)
            {
                case FluidDragNativeMethods.WM_LBUTTONDOWN:
                    HandleMouseDown(pt, settings);
                    break;

                case FluidDragNativeMethods.WM_MOUSEMOVE:
                    HandleMouseMove(pt, settings);
                    break;

                case FluidDragNativeMethods.WM_LBUTTONUP:
                case FluidDragNativeMethods.WM_RBUTTONDOWN:
                case FluidDragNativeMethods.WM_MBUTTONDOWN:
                    ResetDragState();
                    break;
            }
        }

        return FluidDragNativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void HandleMouseDown(FluidDragNativeMethods.POINT pt, FluidDragSettings settings)
    {
        ResetDragState();

        if (!CheckModifierKeys(settings.EnableModifierKey, settings.DisableModifierKey))
        {
            return;
        }

        IntPtr rawHwnd = FluidDragNativeMethods.WindowFromPoint(pt);
        if (rawHwnd == IntPtr.Zero) return;

        // 小ウィンドウ（Discord画面共有小窓、PIP等）を優先特定
        IntPtr targetHwnd = FindDraggableTargetHwnd(rawHwnd, pt);
        if (targetHwnd == IntPtr.Zero) return;

        // 自プロセスのウィンドウやシェル・デスクトップ・エクスプローラー系コントロールを除外
        if (IsSpecialOrExcludedWindow(rawHwnd, targetHwnd, settings))
        {
            return;
        }

        // 最大化ウィンドウの除外チェック
        if (settings.ExcludeMaximizedWindows && IsWindowMaximized(targetHwnd))
        {
            return;
        }

        // フルスクリーンアプリの除外チェック
        if (settings.DisableWhenFullscreen && IsWindowFullscreen(targetHwnd))
        {
            return;
        }

        // マウスカーソルが「通常（矢印）」であるか厳格に判定（Hand, IBeam, Size 等の操作中は絶対にドラッグしない）
        if (!CursorHelper.IsNormalCursor())
        {
            return;
        }

        // ドラッグ候補として待機状態にする
        if (FluidDragNativeMethods.GetWindowRect(targetHwnd, out var rect))
        {
            _isPendingDrag = true;
            _startMousePoint = pt;
            _startWindowRect = rect;
            _targetHwnd = targetHwnd;
        }
    }

    private static IntPtr FindDraggableTargetHwnd(IntPtr rawHwnd, FluidDragNativeMethods.POINT pt)
    {
        IntPtr rootHwnd = FluidDragNativeMethods.GetAncestor(rawHwnd, FluidDragNativeMethods.GA_ROOT);
        if (rootHwnd == IntPtr.Zero) rootHwnd = rawHwnd;

        // 独立したフローティング小ウィンドウ(PIP/画面共有小窓等)の判定
        // ※ WS_CHILD を持つ内部コントロール/パネルは除外
        if (rawHwnd != rootHwnd)
        {
            int style = FluidDragNativeMethods.GetWindowLong(rawHwnd, FluidDragNativeMethods.GWL_STYLE);
            bool isChild = (style & 0x40000000) != 0; // WS_CHILD = 0x40000000
            if (!isChild && IsSmallOrPipWindow(rawHwnd))
            {
                return rawHwnd;
            }
        }

        return rootHwnd;
    }

    private static bool IsSmallOrPipWindow(IntPtr hwnd)
    {
        if (FluidDragNativeMethods.GetWindowRect(hwnd, out var r))
        {
            int w = r.Width;
            int h = r.Height;
            return w > 80 && h > 80 && w < 1000 && h < 750;
        }
        return false;
    }

    private void HandleMouseMove(FluidDragNativeMethods.POINT pt, FluidDragSettings settings)
    {
        if (_isPendingDrag && _targetHwnd != IntPtr.Zero)
        {
            int dX = pt.x - _startMousePoint.x;
            int dY = pt.y - _startMousePoint.y;
            double distance = Math.Sqrt(dX * dX + dY * dY);

            if (distance >= settings.DragThresholdPixels)
            {
                _isDragging = true;
                _isPendingDrag = false;
            }
        }

        if (_isDragging && _targetHwnd != IntPtr.Zero)
        {
            int deltaX = pt.x - _startMousePoint.x;
            int deltaY = pt.y - _startMousePoint.y;

            int newX = _startWindowRect.Left + deltaX;
            int newY = _startWindowRect.Top + deltaY;

            FluidDragNativeMethods.SetWindowPos(
                _targetHwnd,
                IntPtr.Zero,
                newX,
                newY,
                0,
                0,
                FluidDragNativeMethods.SWP_NOSIZE | FluidDragNativeMethods.SWP_NOZORDER | FluidDragNativeMethods.SWP_NOACTIVATE
            );
        }
    }

    private bool CheckModifierKeys(ModifierKeyMode enableKey, ModifierKeyMode disableKey)
    {
        // 1. 無効化キーが押されている場合はドラッグ不許可（押している間だけ無効化）
        if (disableKey != ModifierKeyMode.None && IsKeyPressed(disableKey))
        {
            return false;
        }

        // 2. 有効化キーの判定（押している間だけ有効化。None の場合は常時有効）
        if (enableKey == ModifierKeyMode.None)
        {
            return true;
        }

        return IsKeyPressed(enableKey);
    }

    private bool IsKeyPressed(ModifierKeyMode mode)
    {
        return mode switch
        {
            ModifierKeyMode.Alt => (FluidDragNativeMethods.GetAsyncKeyState(FluidDragNativeMethods.VK_MENU) & 0x8000) != 0,
            ModifierKeyMode.Win => (FluidDragNativeMethods.GetAsyncKeyState(FluidDragNativeMethods.VK_LWIN) & 0x8000) != 0 ||
                                   (FluidDragNativeMethods.GetAsyncKeyState(FluidDragNativeMethods.VK_RWIN) & 0x8000) != 0,
            ModifierKeyMode.Ctrl => (FluidDragNativeMethods.GetAsyncKeyState(FluidDragNativeMethods.VK_CONTROL) & 0x8000) != 0,
            ModifierKeyMode.Shift => (FluidDragNativeMethods.GetAsyncKeyState(FluidDragNativeMethods.VK_SHIFT) & 0x8000) != 0,
            _ => false
        };
    }

    private bool IsSpecialOrExcludedWindow(IntPtr rawHwnd, IntPtr rootHwnd, FluidDragSettings settings)
    {
        // デスクトップやタスクバー
        IntPtr desktop = FluidDragNativeMethods.GetDesktopWindow();
        IntPtr shell = FluidDragNativeMethods.GetShellWindow();
        if (rootHwnd == desktop || rootHwnd == shell || rawHwnd == desktop || rawHwnd == shell) return true;

        // クリックされたコントロールまたはルートのウィンドウクラス名チェック
        string rawClass = FluidDragNativeMethods.GetClassName(rawHwnd);
        string rootClass = FluidDragNativeMethods.GetClassName(rootHwnd);

        // アプリ内パラメータ・UIコントロール（音量バー、スライダー、スクロールバー、ボタン、テキスト等）のドラッグを完全除外
        if (rawClass is "msctls_trackbar32" or "ScrollBar" or "msctls_progress32" or "Slider" or "Button" or "Edit" or "RichEdit" or "RichEdit20W" or "RichEdit20A" or "RICHEDIT50W" or "ComboBox" or "ListBox" or "SysTabControl32" or "ToolbarWindow32" or "SysHeader32" or "SysDateTimePick32" or "SysMonthCal32")
        {
            return true;
        }

        // シェル・デスクトップ・タスクバー・トレイ領域
        if (rawClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TrayNotifyWnd" or "TrayClockWClass" or "MSTaskListWClass" or "MSTaskSwWClass" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow" or "Xaml_WindowedPopupClass" ||
            rootClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TrayNotifyWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow")
        {
            return true;
        }

            // エクスプローラーのファイル一覧・ツリー・シェルビュー（ドラッグ＆ドロップ妨害防止）
            if (rawClass is "DirectUIHWND" or "SysListView32" or "SysTreeView32" or "SHELLDLL_DefView" or "CabinetWClass")
            {
                return true;
            }

            // 自プロセスのチェック
            FluidDragNativeMethods.GetWindowThreadProcessId(rootHwnd, out uint pid);
            if (pid == Process.GetCurrentProcess().Id)
            {
                return true;
            }

            // プロセス名除外（ブラックリスト）チェック
            string processName = FluidDragNativeMethods.GetProcessNameFromHwnd(rootHwnd);
            if (!string.IsNullOrEmpty(processName))
            {
                if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(processName, "explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                foreach (var excludedProc in settings.ExcludedProcesses)
                {
                    if (string.Equals(processName, excludedProc, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(processName + ".exe", excludedProc, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            // ウィンドウタイトル除外チェック
            string title = FluidDragNativeMethods.GetWindowTitle(rootHwnd);
            if (!string.IsNullOrEmpty(title))
            {
                foreach (var excludedTitle in settings.ExcludedWindowTitles)
                {
                    if (!string.IsNullOrWhiteSpace(excludedTitle) &&
                        title.Contains(excludedTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

    private bool IsWindowMaximized(IntPtr hwnd)
    {
        if (FluidDragNativeMethods.IsZoomed(hwnd))
        {
            return true;
        }

        IntPtr hMonitor = FluidDragNativeMethods.MonitorFromWindow(hwnd, FluidDragNativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero)
        {
            var mi = new FluidDragNativeMethods.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(FluidDragNativeMethods.MONITORINFO));
            if (FluidDragNativeMethods.GetMonitorInfo(hMonitor, ref mi) && FluidDragNativeMethods.GetWindowRect(hwnd, out var rect))
            {
                int workW = mi.rcWork.Width;
                int workH = mi.rcWork.Height;
                int winW = rect.Width;
                int winH = rect.Height;

                if (winW >= workW - 25 && winH >= workH - 25 &&
                    rect.Left <= mi.rcWork.Left + 15 && rect.Top <= mi.rcWork.Top + 15)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsWindowFullscreen(IntPtr hwnd)
    {
        IntPtr hMonitor = FluidDragNativeMethods.MonitorFromWindow(hwnd, FluidDragNativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return false;

        var mi = new FluidDragNativeMethods.MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(FluidDragNativeMethods.MONITORINFO));
        if (!FluidDragNativeMethods.GetMonitorInfo(hMonitor, ref mi)) return false;

        if (!FluidDragNativeMethods.GetWindowRect(hwnd, out var rect)) return false;

        return rect.Left <= mi.rcMonitor.Left + 10 &&
               rect.Top <= mi.rcMonitor.Top + 10 &&
               rect.Right >= mi.rcMonitor.Right - 10 &&
               rect.Bottom >= mi.rcMonitor.Bottom - 10;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
