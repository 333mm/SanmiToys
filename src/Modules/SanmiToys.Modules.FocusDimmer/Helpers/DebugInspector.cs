using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SanmiToys.Modules.FocusDimmer.Core;
using SanmiToys.Modules.FocusDimmer.Models;
using SanmiToys.Modules.FocusDimmer.Views;

namespace SanmiToys.Modules.FocusDimmer.Helpers;

public class DebugInspector : IDisposable
{
    private readonly FocusDimmerSettings _settings;
    private readonly DispatcherTimer _timer;
    private DebugInspectorWindow? _window;
    private HighlightOverlayWindow? _highlightOverlay;
    private bool _isTracking;
    private bool _isSelectionMode;

    public event EventHandler? StopRequested;
    public event EventHandler<WindowData>? SelectedWindowCaptured;

    private IntPtr _hwndSelf = IntPtr.Zero;
    private IntPtr _hookId = IntPtr.Zero;
    private FocusDimmerNativeMethods.LowLevelMouseProc? _mouseProc;
    private List<WindowData> _currentWindows = new();

    public DebugInspector(FocusDimmerSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        if (_isTracking) return;
        _isTracking = true;
        _isSelectionMode = false;

        _highlightOverlay = new HighlightOverlayWindow();
        _highlightOverlay.Show();

        _window = new DebugInspectorWindow();
        _window.WindowSelected += (s, data) =>
        {
            SelectedWindowCaptured?.Invoke(this, data);
        };
        _window.WindowHovered += (s, data) =>
        {
            if (data != null)
            {
                _highlightOverlay?.UpdateHighlight(data.WindowRect, data.ReasonBadge, $"{data.ProcessName} - {data.Title}");
            }
        };
        _window.CloseRequested += (s, e) =>
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            Stop();
        };

        var helper = new WindowInteropHelper(_window);
        _hwndSelf = helper.EnsureHandle();

        FocusDimmerNativeMethods.SetProp(_hwndSelf, "FocusDimmerInspector", new IntPtr(1));

        _mouseProc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule?.ModuleName != null)
        {
            _hookId = FocusDimmerNativeMethods.SetWindowsHookEx(
                FocusDimmerNativeMethods.WH_MOUSE_LL,
                _mouseProc,
                FocusDimmerNativeMethods.GetModuleHandle(curModule.ModuleName),
                0);
        }

        _window.Show();
        _timer.Start();
    }

    public void Stop()
    {
        if (!_isTracking) return;
        _isTracking = false;
        _isSelectionMode = false;
        _timer.Stop();

        if (_hookId != IntPtr.Zero)
        {
            FocusDimmerNativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        if (_hwndSelf != IntPtr.Zero)
        {
            FocusDimmerNativeMethods.RemoveProp(_hwndSelf, "FocusDimmerInspector");
        }

        _highlightOverlay?.Close();
        _highlightOverlay = null;

        _window?.Close();
        _window = null;
        _hwndSelf = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)FocusDimmerNativeMethods.WM_LBUTTONDOWN)
        {
            if (!_isSelectionMode && _currentWindows.Count > 0)
            {
                _isSelectionMode = true;

                if (_hookId != IntPtr.Zero)
                {
                    FocusDimmerNativeMethods.UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }

                _window?.Dispatcher.Invoke(() =>
                {
                    if (_window == null) return;
                    _window.UpdateStatus("クリックして対象ウィンドウを選択 (Escで終了)");
                    _window.IsHitTestVisible = true;
                    _window.Topmost = true;
                    _window.Activate();
                });

                return (IntPtr)1; // クリックを消費して固定
            }
        }
        return FocusDimmerNativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_window == null) return;

        if ((FocusDimmerNativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0) // VK_ESCAPE
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            Stop();
            return;
        }

        if (_isSelectionMode) return;

        UpdateInspection();
    }

    private void UpdateInspection()
    {
        if (_window == null) return;
        if (!FocusDimmerNativeMethods.GetCursorPos(out var pt)) return;

        double dpiScaleX = 1.0;
        double dpiScaleY = 1.0;
        var source = PresentationSource.FromVisual(_window);
        if (source?.CompositionTarget != null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        double left = (pt.X / dpiScaleX) + 24;
        double top = (pt.Y / dpiScaleY) + 24;

        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double screenHeight = SystemParameters.PrimaryScreenHeight;

        double winWidth = _window.ActualWidth > 0 ? _window.ActualWidth : 380;
        double winHeight = _window.ActualHeight > 0 ? _window.ActualHeight : 320;

        if (left + winWidth > screenWidth) left = (pt.X / dpiScaleX) - winWidth - 24;
        if (top + winHeight > screenHeight) top = (pt.Y / dpiScaleY) - winHeight - 24;

        _window.Left = Math.Max(0, left);
        _window.Top = Math.Max(0, top);

        IntPtr foregroundHwnd = FocusDimmerNativeMethods.GetForegroundWindow();
        var alwaysBrightSet = new HashSet<string>((_settings.AlwaysBrightList ?? "")
            .Split(',')
            .Select(x => x.Trim().ToLower())
            .Where(x => !string.IsNullOrEmpty(x)));

        var candidateWindows = new List<WindowData>();
        int count = 0;

        uint selfPid = (uint)Process.GetCurrentProcess().Id;

        FocusDimmerNativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (FocusDimmerNativeMethods.GetProp(hwnd, "FocusDimmerInspector") != IntPtr.Zero) return true;
            if (!FocusDimmerNativeMethods.IsWindowVisible(hwnd)) return true;

            FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            // SanmiToys 自身の全ウィンドウを完全に除外
            if (pid == selfPid) return true;

            string processName = ProcessInfoHelper.GetProcessName(pid);
            if (processName.Equals("SanmiToys", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("SanmiToys.Host", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (FocusDimmerNativeMethods.GetWindowRect(hwnd, out var r))
            {
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return true;

                if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                {
                    var className = new StringBuilder(256);
                    FocusDimmerNativeMethods.GetClassName(hwnd, className, className.Capacity);
                    string cName = className.ToString();

                    var title = new StringBuilder(256);
                    FocusDimmerNativeMethods.GetWindowText(hwnd, title, title.Capacity);
                    string winTitle = title.ToString();

                    int exStyle = FocusDimmerNativeMethods.GetWindowLong(hwnd, FocusDimmerNativeMethods.GWL_EXSTYLE);
                    bool isTopmost = (exStyle & FocusDimmerNativeMethods.WS_EX_TOPMOST) != 0;
                    bool isTaskbar = cName.Contains("TrayWnd", StringComparison.OrdinalIgnoreCase);
                    bool isForeground = (hwnd == foregroundHwnd);
                    bool isAlwaysBright = alwaysBrightSet.Contains(processName.ToLower());

                    // 明るい理由の判定
                    string reasonBadge = "明るい領域";
                    Brush bgBrush = new SolidColorBrush(Color.FromArgb(40, 0, 210, 255));
                    Brush fgBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255));

                    if (isForeground)
                    {
                        reasonBadge = "⭐ アクティブ";
                        bgBrush = new SolidColorBrush(Color.FromArgb(50, 0, 255, 128));
                        fgBrush = new SolidColorBrush(Color.FromRgb(0, 255, 128));
                    }
                    else if (isAlwaysBright)
                    {
                        reasonBadge = "🟢 常時明るい設定";
                        bgBrush = new SolidColorBrush(Color.FromArgb(50, 100, 255, 100));
                        fgBrush = new SolidColorBrush(Color.FromRgb(100, 255, 100));
                    }
                    else if (isTopmost)
                    {
                        reasonBadge = "📌 最前面 (Topmost)";
                        bgBrush = new SolidColorBrush(Color.FromArgb(50, 255, 180, 0));
                        fgBrush = new SolidColorBrush(Color.FromRgb(255, 195, 0));
                    }
                    else if (isTaskbar)
                    {
                        reasonBadge = "📊 タスクバー";
                        bgBrush = new SolidColorBrush(Color.FromArgb(50, 180, 100, 255));
                        fgBrush = new SolidColorBrush(Color.FromRgb(200, 140, 255));
                    }

                    var flags = new List<string>();
                    if ((exStyle & FocusDimmerNativeMethods.WS_EX_TRANSPARENT) != 0) flags.Add("透過");
                    if ((exStyle & FocusDimmerNativeMethods.WS_EX_LAYERED) != 0) flags.Add("レイヤード");
                    if ((exStyle & FocusDimmerNativeMethods.WS_EX_NOACTIVATE) != 0) flags.Add("非アクティブ");
                    if ((exStyle & FocusDimmerNativeMethods.WS_EX_TOOLWINDOW) != 0) flags.Add("ツール窓");
                    if (isTopmost) flags.Add("最前面");

                    // 仮想スクリーン DPI 補正
                    var rectWpf = new Rect(r.Left / dpiScaleX, r.Top / dpiScaleY, w / dpiScaleX, h / dpiScaleY);

                    candidateWindows.Add(new WindowData
                    {
                        Index = count++,
                        Hwnd = hwnd,
                        ProcessName = processName,
                        Title = winTitle,
                        ClassName = cName,
                        RectString = $"{r.Left},{r.Top} ({w}x{h})",
                        WindowRect = rectWpf,
                        Flags = flags.Count > 0 ? string.Join(", ", flags) : "標準",
                        ReasonBadge = reasonBadge,
                        BadgeBackgroundBrush = bgBrush,
                        BadgeForegroundBrush = fgBrush
                    });

                    if (count >= 10) return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        _currentWindows = candidateWindows;
        _window.UpdateList(_currentWindows);

        // カーソル下の最前面の明るい領域をネオンアウトラインで即座に強調囲い
        var topBright = _currentWindows.FirstOrDefault();
        if (topBright != null)
        {
            _highlightOverlay?.UpdateHighlight(topBright.WindowRect, topBright.ReasonBadge, $"{topBright.ProcessName} - {topBright.Title}");
        }
        else
        {
            _highlightOverlay?.ClearHighlight();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
