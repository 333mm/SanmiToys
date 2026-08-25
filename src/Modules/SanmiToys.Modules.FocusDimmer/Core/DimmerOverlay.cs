using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using SanmiToys.Modules.FocusDimmer.Models;

namespace SanmiToys.Modules.FocusDimmer.Core;

public class DimmerOverlay : IDisposable
{
    public MonitorProfile LinkedProfile { get; private set; }
    private readonly Func<FocusDimmerSettings> _settingsAccessor;
    private Window? _window;
    private Path? _path;
    private SolidColorBrush? _brush;
    private CombinedGeometry? _finalGeo;
    private GeometryGroup? _holesGroup;
    private RectangleGeometry? _bgRect;
    private IntPtr _myHandle = IntPtr.Zero;

    private IntPtr _lastTargetHwnd = IntPtr.Zero;
    private bool _isCurrentlyActiveState = false;
    private bool _wasIdle = false;

    private readonly DispatcherTimer _delayTimer;
    private readonly List<FocusDimmerNativeMethods.RECT> _reusableSpecialWindows = new();
    private FocusDimmerNativeMethods.RECT _lastRenderedTargetRect = new();
    private IntPtr _lastRenderedTargetHwnd = IntPtr.Zero;
    private bool _lastRenderedForceNoHoles;
    private bool _hasRenderedHoles;
    private DateTime _lastSpecialWindowsScanUtc = DateTime.MinValue;
    private bool _disposed = false;

    public DimmerOverlay(MonitorProfile profile, Func<FocusDimmerSettings> settingsAccessor)
    {
        LinkedProfile = profile;
        _settingsAccessor = settingsAccessor;

        _brush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        _holesGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
        _bgRect = new RectangleGeometry(new Rect(-20000, -20000, 60000, 60000));
        _finalGeo = new CombinedGeometry(GeometryCombineMode.Exclude, _bgRect, _holesGroup);

        _path = new Path { Data = _finalGeo, Fill = _brush };
        RenderOptions.SetEdgeMode(_path, EdgeMode.Aliased);

        var bounds = profile.ScreenRef?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Content = _path,
            IsHitTestVisible = false,
            Left = bounds.Left - 1,
            Top = bounds.Top - 1,
            Width = bounds.Width + 2,
            Height = bounds.Height + 2
        };

        _window.SourceInitialized += (s, e) =>
        {
            var helper = new WindowInteropHelper(_window);
            _myHandle = helper.Handle;
            ApplyClickThroughStyle();
            WindowHelper.DisableBackdropAndBlur(_myHandle);
        };

        _window.Loaded += (s, e) =>
        {
            if (_myHandle == IntPtr.Zero)
            {
                var helper = new WindowInteropHelper(_window);
                _myHandle = helper.Handle;
            }
            ApplyClickThroughStyle();
            WindowHelper.DisableBackdropAndBlur(_myHandle);
            UpdateWindowBounds();
        };

        LinkedProfile.PropertyChanged += OnProfilePropertyChanged;

        _delayTimer = new DispatcherTimer();
        _delayTimer.Tick += DelayTimer_Tick;
    }

    private void ApplyClickThroughStyle()
    {
        if (_myHandle == IntPtr.Zero) return;
        int exStyle = FocusDimmerNativeMethods.GetWindowLong(_myHandle, FocusDimmerNativeMethods.GWL_EXSTYLE);
        int newExStyle = exStyle | FocusDimmerNativeMethods.WS_EX_LAYERED 
                                 | FocusDimmerNativeMethods.WS_EX_TRANSPARENT 
                                 | FocusDimmerNativeMethods.WS_EX_TOOLWINDOW 
                                 | FocusDimmerNativeMethods.WS_EX_NOACTIVATE;
        FocusDimmerNativeMethods.SetWindowLong(_myHandle, FocusDimmerNativeMethods.GWL_EXSTYLE, newExStyle);
        FocusDimmerNativeMethods.SetWindowPos(_myHandle, IntPtr.Zero, 0, 0, 0, 0, 
            FocusDimmerNativeMethods.SWP_NOMOVE | FocusDimmerNativeMethods.SWP_NOSIZE | FocusDimmerNativeMethods.SWP_NOZORDER | FocusDimmerNativeMethods.SWP_FRAMECHANGED | FocusDimmerNativeMethods.SWP_NOACTIVATE);
    }

    private void OnProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorProfile.Opacity) || e.PropertyName == nameof(MonitorProfile.OverlayColorHex))
        {
            ApplyAppearanceImmediately();
        }
    }

    public void Show() => _window?.Show();
    public void SetVisibility(bool visible) { if (_window != null) _window.Visibility = visible ? Visibility.Visible : Visibility.Hidden; }

    public void EnsureTopmost()
    {
        if (_window == null || _myHandle == IntPtr.Zero) return;
        
        FocusDimmerNativeMethods.SetWindowPos(_myHandle, new IntPtr(-1), 0, 0, 0, 0, 
            FocusDimmerNativeMethods.SWP_NOSIZE | FocusDimmerNativeMethods.SWP_NOMOVE | FocusDimmerNativeMethods.SWP_NOACTIVATE);

        // タスクバーを除外する場合、タスクバー（Shell_TrayWnd）をオーバーレイの前面（最前面）に配置
        // これにより、TranslucentTB や RoundedTB の透過アイランドUIのみが100%自然・正確に明るく表示される
        if (LinkedProfile.ExcludeTaskbar)
        {
            IntPtr primaryTray = FocusDimmerNativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero && FocusDimmerNativeMethods.IsWindowVisible(primaryTray))
            {
                FocusDimmerNativeMethods.SetWindowPos(primaryTray, new IntPtr(-1), 0, 0, 0, 0, 
                    FocusDimmerNativeMethods.SWP_NOSIZE | FocusDimmerNativeMethods.SWP_NOMOVE | FocusDimmerNativeMethods.SWP_NOACTIVATE);
            }

            IntPtr secTray = IntPtr.Zero;
            while ((secTray = FocusDimmerNativeMethods.FindWindowEx(IntPtr.Zero, secTray, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                if (FocusDimmerNativeMethods.IsWindowVisible(secTray))
                {
                    FocusDimmerNativeMethods.SetWindowPos(secTray, new IntPtr(-1), 0, 0, 0, 0, 
                        FocusDimmerNativeMethods.SWP_NOSIZE | FocusDimmerNativeMethods.SWP_NOMOVE | FocusDimmerNativeMethods.SWP_NOACTIVATE);
                }
            }
        }
    }

    private void UpdateWindowBounds()
    {
        var win = _window;
        if (win == null) return;
        var source = PresentationSource.FromVisual(win);
        if (source?.CompositionTarget == null) return;
        double scaleX = source.CompositionTarget.TransformToDevice.M11;
        double scaleY = source.CompositionTarget.TransformToDevice.M22;
        var bounds = LinkedProfile.ScreenRef?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        win.Left = (bounds.Left - 1) / scaleX;
        win.Top = (bounds.Top - 1) / scaleY;
        win.Width = (bounds.Width + 2) / scaleX;
        win.Height = (bounds.Height + 2) / scaleY;
        if (_bgRect != null) _bgRect.Rect = new Rect(0, 0, win.Width, win.Height);
    }

    private Color GetBaseColor()
    {
        try { return (Color)ColorConverter.ConvertFromString(LinkedProfile.OverlayColorHex); }
        catch { return Colors.Black; }
    }

    public void UpdateState(IntPtr foregroundHwnd, bool shouldDim, bool windowChanged, bool forceNoHoles, bool isIdle, bool isMoving = false)
    {
        if (_isCurrentlyActiveState != shouldDim)
        {
            _isCurrentlyActiveState = shouldDim;

            if (shouldDim)
            {
                _delayTimer.Stop();
                if (isIdle)
                {
                    FadeToDark(1.0, LinkedProfile.IdleDimOpacity);
                }
                else if (LinkedProfile.DelayDarken > 0.05)
                {
                    _delayTimer.Interval = TimeSpan.FromSeconds(LinkedProfile.DelayDarken);
                    _delayTimer.Start();
                    _brush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    if (_brush != null) _brush.Color = Color.FromArgb(0, 0, 0, 0);
                }
                else
                {
                    FadeToDark(LinkedProfile.DurationDarken);
                }
            }
            else
            {
                _delayTimer.Stop();
                double durationMs = _wasIdle ? 1000.0 : LinkedProfile.DurationBrighten;
                double ms = durationMs > 10 ? durationMs : durationMs * 1000.0;
                if (ms > 10)
                {
                    var fadeAnim = new ColorAnimation
                    {
                        To = Color.FromArgb(0, 0, 0, 0),
                        Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    _brush?.BeginAnimation(SolidColorBrush.ColorProperty, fadeAnim);
                }
                else
                {
                    _brush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    if (_brush != null) _brush.Color = Color.FromArgb(0, 0, 0, 0);
                }
            }
        }
        // 既に減光中の場合は、ウィンドウ切り替え時に明るくリセットせず、減光の暗さをそのまま維持して穴あけ位置のみを追従

        _wasIdle = isIdle;
        UpdateHoles(foregroundHwnd, forceNoHoles, isMoving);
    }

    private void StartBreathSequence()
    {
        _delayTimer.Stop();

        double fadeOutMs = LinkedProfile.DurationBrighten;
        double ms = fadeOutMs > 10 ? fadeOutMs : fadeOutMs * 1000.0;
        if (ms <= 10)
        {
            _brush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
            if (_brush != null) _brush.Color = Color.FromArgb(0, 0, 0, 0);
            _delayTimer.Interval = TimeSpan.FromSeconds(LinkedProfile.DelayDarken);
            _delayTimer.Start();
            return;
        }

        var fadeOut = new ColorAnimation
        {
            To = Color.FromArgb(0, 0, 0, 0),
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            FillBehavior = FillBehavior.HoldEnd
        };

        fadeOut.Completed += (s, e) =>
        {
            _delayTimer.Interval = TimeSpan.FromSeconds(LinkedProfile.DelayDarken);
            _delayTimer.Start();
        };

        _brush?.BeginAnimation(SolidColorBrush.ColorProperty, fadeOut);
    }

    private void DelayTimer_Tick(object? sender, EventArgs e)
    {
        _delayTimer.Stop();
        FadeToDark(LinkedProfile.DurationDarken);
    }

    private void FadeToDark(double duration, double? targetOpacity = null)
    {
        double op = targetOpacity ?? LinkedProfile.Opacity;
        byte targetAlpha = (byte)(op / 100.0 * 255);
        var c = GetBaseColor();
        double ms = duration > 10 ? duration : duration * 1000.0;
        if (ms <= 10)
        {
            _brush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
            if (_brush != null) _brush.Color = Color.FromArgb(targetAlpha, c.R, c.G, c.B);
            return;
        }

        var anim = new ColorAnimation
        {
            To = Color.FromArgb(targetAlpha, c.R, c.G, c.B),
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            FillBehavior = FillBehavior.HoldEnd
        };
        _brush?.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void ApplyAppearanceImmediately()
    {
        _delayTimer.Stop();
        _brush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        if (_isCurrentlyActiveState)
        {
            byte targetAlpha = (byte)(LinkedProfile.Opacity / 100.0 * 255);
            var c = GetBaseColor();
            if (_brush != null) _brush.Color = Color.FromArgb(targetAlpha, c.R, c.G, c.B);
        }
    }

    private int _shadowInsetLeft = 0;
    private int _shadowInsetTop = 0;
    private int _shadowInsetRight = 0;
    private int _shadowInsetBottom = 0;

    private void UpdateHoles(IntPtr targetHwnd, bool forceNoHoles, bool isMoving)
    {
        FocusDimmerNativeMethods.RECT currentRect = new();
        if (targetHwnd != IntPtr.Zero)
        {
            if (isMoving)
            {
                // ドラッグ移動中はリアルタイムな GetWindowRect に計測済みのタイトインセットを適用して一貫した枠サイズを維持
                if (FocusDimmerNativeMethods.GetWindowRect(targetHwnd, out var rawRect))
                {
                    currentRect = new FocusDimmerNativeMethods.RECT
                    {
                        Left = rawRect.Left + _shadowInsetLeft,
                        Top = rawRect.Top + _shadowInsetTop,
                        Right = rawRect.Right - _shadowInsetRight,
                        Bottom = rawRect.Bottom - _shadowInsetBottom
                    };
                }
            }
            else
            {
                // 静止時はタイト枠を取得し、インセット差分を更新
                if (FocusDimmerNativeMethods.GetTightWindowRect(targetHwnd, out var tightRect) && FocusDimmerNativeMethods.GetWindowRect(targetHwnd, out var rawRect))
                {
                    currentRect = tightRect;
                    _shadowInsetLeft = Math.Max(0, tightRect.Left - rawRect.Left);
                    _shadowInsetTop = Math.Max(0, tightRect.Top - rawRect.Top);
                    _shadowInsetRight = Math.Max(0, rawRect.Right - tightRect.Right);
                    _shadowInsetBottom = Math.Max(0, rawRect.Bottom - tightRect.Bottom);
                }
                else if (!FocusDimmerNativeMethods.GetWindowRect(targetHwnd, out currentRect))
                {
                    currentRect = new FocusDimmerNativeMethods.RECT();
                }
            }
        }

        bool hasVisibleDimmer = _isCurrentlyActiveState || (_brush != null && _brush.Color.A > 0);
        // 全ウィンドウ列挙は高コストで、モニターごとに実行するとゲームの描画を阻害する。
        // フォーカス移動後も含め、最大でも 1 秒に 2 回だけ更新する。
        bool needsSpecialWindowsScan = !isMoving && hasVisibleDimmer &&
            DateTime.UtcNow - _lastSpecialWindowsScanUtc >= TimeSpan.FromMilliseconds(500);

        if (needsSpecialWindowsScan)
        {
            _lastSpecialWindowsScanUtc = DateTime.UtcNow;
            _reusableSpecialWindows.Clear();
            var specialWindows = _reusableSpecialWindows;

            // 全ウィンドウ列挙による DimDesktopOnly および Topmost 除外
            if (hasVisibleDimmer)
            {
                FocusDimmerNativeMethods.EnumWindows((hwnd, lp) =>
                {
                    if (FocusDimmerNativeMethods.IsWindowVisible(hwnd) && hwnd != _myHandle && hwnd != targetHwnd)
                    {
                        if (FocusDimmerNativeMethods.IsWindowCloaked(hwnd) || FocusDimmerNativeMethods.IsIconic(hwnd)) return true;

                        bool shouldAdd = false;

                        if (IsTaskbarWindow(hwnd))
                        {
                            // タスクバーは UpdateHoles の AddTaskbarHoles で直接処理するためスキップ
                            return true;
                        }
                        else if (LinkedProfile.DimDesktopOnly && !forceNoHoles)
                        {
                            if (!IsDesktopWindow(hwnd))
                            {
                                bool isMenu = WindowHelper.IsMenuOrPopupEx(hwnd);
                                bool isDialog = IsDialogWindow(hwnd);
                                if (isDialog || (!isMenu && !IsAlwaysDarkWindow(hwnd)))
                                {
                                    shouldAdd = true;
                                }
                            }
                        }
                        else
                        {
                            bool isMenu = WindowHelper.IsMenuOrPopupEx(hwnd);
                            if (!isMenu && IsAlwaysDarkWindow(hwnd)) return true;

                            bool isBright = isMenu || IsAlwaysBrightWindow(hwnd);
                            if (forceNoHoles)
                            {
                                if (isBright) shouldAdd = true;
                            }
                            else
                            {
                                if (isBright) shouldAdd = true;
                                else if (LinkedProfile.ExcludeTopmost && ((FocusDimmerNativeMethods.GetWindowLong(hwnd, FocusDimmerNativeMethods.GWL_EXSTYLE) & FocusDimmerNativeMethods.WS_EX_TOPMOST) != 0))
                                {
                                    shouldAdd = true;
                                }
                            }
                        }

                        if (shouldAdd)
                        {
                            FocusDimmerNativeMethods.RECT r = new();
                            bool s = false;
                            if (LinkedProfile.UseTightFrame) s = FocusDimmerNativeMethods.GetTightWindowRect(hwnd, out r);
                            if (!s) FocusDimmerNativeMethods.GetWindowRect(hwnd, out r);

                            if (r.Right - r.Left > 20 && r.Bottom - r.Top > 20)
                            {
                                specialWindows.Add(r);
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
        }

        // 同じ穴構成を再描画しない。WPF の Geometry を毎 Tick 作り直すと透明オーバーレイ
        // の合成負荷が常時発生するため、位置や除外対象が変わった時だけ更新する。
        if (!needsSpecialWindowsScan && _hasRenderedHoles &&
            targetHwnd == _lastRenderedTargetHwnd &&
            currentRect.Equals(_lastRenderedTargetRect) &&
            forceNoHoles == _lastRenderedForceNoHoles)
        {
            return;
        }

        _lastRenderedTargetHwnd = targetHwnd;
        _lastRenderedTargetRect = currentRect;
        _lastRenderedForceNoHoles = forceNoHoles;
        _hasRenderedHoles = true;

        _holesGroup?.Children.Clear();
        if (_window == null) return;

        var source = PresentationSource.FromVisual(_window);
        if (source?.CompositionTarget == null) return;
        double scaleX = source.CompositionTarget.TransformToDevice.M11;
        double scaleY = source.CompositionTarget.TransformToDevice.M22;

        if (hasVisibleDimmer)
        {
            if (!forceNoHoles && targetHwnd != IntPtr.Zero)
            {
                AddHoleForRect(currentRect, LinkedProfile.Margin, scaleX, scaleY);
            }

            // 他の明るいポップアップ・メニュー等のウィンドウを維持
            foreach (var r in _reusableSpecialWindows)
            {
                AddHoleForRect(r, 0, scaleX, scaleY);
            }
        }
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var sb = new StringBuilder(256);
        FocusDimmerNativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var sb = new StringBuilder(256);
        FocusDimmerNativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls is "Progman" or "WorkerW";
    }

    private static bool IsDialogWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        FocusDimmerNativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() == "#32770";
    }

    private bool IsAlwaysDarkWindow(IntPtr hwnd)
    {
        var settings = _settingsAccessor();
        if (!string.IsNullOrWhiteSpace(settings.AlwaysDarkList))
        {
            FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            string proc = ProcessInfoHelper.GetProcessName(pid);
            var darks = settings.AlwaysDarkList.Split(',').Select(x => x.Trim().ToLower().Replace(".exe", ""));
            if (darks.Contains(proc)) return true;
        }
        return false;
    }

    private bool IsAlwaysBrightWindow(IntPtr hwnd)
    {
        var settings = _settingsAccessor();
        if (!string.IsNullOrWhiteSpace(settings.AlwaysBrightList))
        {
            FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            string proc = ProcessInfoHelper.GetProcessName(pid);
            var brights = settings.AlwaysBrightList.Split(',').Select(x => x.Trim().ToLower().Replace(".exe", ""));
            if (brights.Contains(proc)) return true;
        }
        return false;
    }

    private void AddHoleForRect(FocusDimmerNativeMethods.RECT r, double margin, double scaleX, double scaleY)
    {
        if (LinkedProfile.ScreenRef == null) return;
        double width = (r.Right - r.Left);
        double height = (r.Bottom - r.Top);
        if (width <= 1 || height <= 1) return;

        double physLeft = r.Left - LinkedProfile.ScreenRef.Bounds.Left;
        double physTop = r.Top - LinkedProfile.ScreenRef.Bounds.Top;

        double left = (physLeft + 1) / scaleX - margin;
        double top = (physTop + 1) / scaleY - margin;
        double w = width / scaleX + (margin * 2);
        double h = height / scaleY + (margin * 2);

        if (left + w > 0 && top + h > 0)
        {
            var rGeo = new RectangleGeometry(new Rect(left, top, w, h));
            _holesGroup?.Children.Add(rGeo);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LinkedProfile.PropertyChanged -= OnProfilePropertyChanged;
        _delayTimer.Stop();

        try { _window?.Close(); } catch { }
        _window = null;
        _path = null;
        _brush = null;
        _holesGroup = null;
    }
}
