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
using SanmiToys.Core.Services;
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
    private int _lastOverlayRevision = -1;
    private DateTime _lastSpecialWindowsScanUtc = DateTime.MinValue;
    private IntPtr _cachedTray = IntPtr.Zero;
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
            Topmost = false,
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
                                 | FocusDimmerNativeMethods.WS_EX_NOACTIVATE
                                 | FocusDimmerNativeMethods.WS_EX_TOPMOST;
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
        else if (e.PropertyName == nameof(MonitorProfile.ExcludeTaskbar))
        {
            UpdateWindowBounds();
            EnsureTopmost();
            _hasRenderedHoles = false;
        }
    }

    public IntPtr Handle => _myHandle;

    public void Show() => _window?.Show();
    public void SetVisibility(bool visible)
    {
        if (_window == null) return;
        var targetVis = visible ? Visibility.Visible : Visibility.Hidden;
        if (_window.Visibility != targetVis)
        {
            _window.Visibility = targetVis;
            if (visible)
            {
                EnsureTopmost();
            }
        }
    }

    public bool IsBehindOverlaysAndTaskbar()
    {
        if (_myHandle == IntPtr.Zero) return true;

        // 登録されたオーバーレイ（OmniGlance等）がすべてDimmerの手前にあるか検証
        var overlayHandles = OverlayRegionRegistry.GetOverlayWindowHandles();
        foreach (var ohwnd in overlayHandles)
        {
            if (ohwnd != IntPtr.Zero && FocusDimmerNativeMethods.IsWindow(ohwnd) && FocusDimmerNativeMethods.IsWindowVisible(ohwnd))
            {
                if (!IsWindowAboveMe(ohwnd)) return false;
            }
        }

        // タスクバー除外設定時は、タスクバーがDimmerの手前にあるか検証
        if (LinkedProfile.ExcludeTaskbar)
        {
            IntPtr primaryTray = FocusDimmerNativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero && FocusDimmerNativeMethods.IsWindowVisible(primaryTray))
            {
                if (!IsWindowAboveMe(primaryTray)) return false;
            }
        }

        return true;
    }

    public bool IsBehindTaskbar() => IsBehindOverlaysAndTaskbar();

    private bool IsWindowAboveMe(IntPtr targetHwnd)
    {
        IntPtr cur = _myHandle;
        int count = 0;
        while (count++ < 30 && (cur = FocusDimmerNativeMethods.GetWindow(cur, FocusDimmerNativeMethods.GW_HWNDPREV)) != IntPtr.Zero)
        {
            if (cur == targetHwnd) return true;
        }

        return false;
    }

    public void EnsureTopmost()
    {
        if (_window == null || _myHandle == IntPtr.Zero) return;

        const uint swpFlags = FocusDimmerNativeMethods.SWP_NOSIZE | 
                              FocusDimmerNativeMethods.SWP_NOMOVE | 
                              FocusDimmerNativeMethods.SWP_NOACTIVATE | 
                              FocusDimmerNativeMethods.SWP_NOOWNERZORDER | 
                              FocusDimmerNativeMethods.SWP_NOREDRAW;

        // 1. まず Dimmer 自身を最前面スタックに配置
        FocusDimmerNativeMethods.SetWindowPos(_myHandle, new IntPtr(-1), 0, 0, 0, 0, swpFlags);

        // 2. タスクバー除外設定時は、タスクバーを Dimmer の手前（最前面）に配置
        if (LinkedProfile.ExcludeTaskbar)
        {
            IntPtr primaryTray = FocusDimmerNativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero && FocusDimmerNativeMethods.IsWindowVisible(primaryTray))
            {
                FocusDimmerNativeMethods.SetWindowPos(primaryTray, new IntPtr(-1), 0, 0, 0, 0, swpFlags);
            }

            IntPtr secTray = IntPtr.Zero;
            while ((secTray = FocusDimmerNativeMethods.FindWindowEx(IntPtr.Zero, secTray, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                if (FocusDimmerNativeMethods.IsWindowVisible(secTray))
                {
                    FocusDimmerNativeMethods.SetWindowPos(secTray, new IntPtr(-1), 0, 0, 0, 0, swpFlags);
                }
            }
        }

        // 3. 登録されたオーバーレイ（OmniGlance等）を Dimmer の手前（最前面）に配置
        var overlayHandles = OverlayRegionRegistry.GetOverlayWindowHandles();
        foreach (var ohwnd in overlayHandles)
        {
            if (ohwnd != IntPtr.Zero && FocusDimmerNativeMethods.IsWindow(ohwnd) && FocusDimmerNativeMethods.IsWindowVisible(ohwnd))
            {
                FocusDimmerNativeMethods.SetWindowPos(ohwnd, new IntPtr(-1), 0, 0, 0, 0, swpFlags);
            }
        }
    }

    private IntPtr GetTrayWindowForThisScreen()
    {
        if (_cachedTray != IntPtr.Zero && FocusDimmerNativeMethods.IsWindow(_cachedTray))
        {
            return _cachedTray;
        }

        var screen = LinkedProfile.ScreenRef;
        if (screen == null) return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;

        if (screen.Primary)
        {
            found = FocusDimmerNativeMethods.FindWindow("Shell_TrayWnd", null);
        }
        else
        {
            // サブモニターの場合、このモニターの領域と交差する Shell_SecondaryTrayWnd を探す
            IntPtr secTray = IntPtr.Zero;
            while ((secTray = FocusDimmerNativeMethods.FindWindowEx(IntPtr.Zero, secTray, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                if (FocusDimmerNativeMethods.IsWindow(secTray))
                {
                    if (FocusDimmerNativeMethods.GetWindowRect(secTray, out var r))
                    {
                        var trayRect = new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                        if (trayRect.IntersectsWith(screen.Bounds))
                        {
                            found = secTray;
                            break;
                        }
                    }
                }
            }

            // サブタスクバーが見つからない場合はプライマリタスクバーにフォールバック
            if (found == IntPtr.Zero)
            {
                found = FocusDimmerNativeMethods.FindWindow("Shell_TrayWnd", null);
            }
        }

        if (found != IntPtr.Zero && FocusDimmerNativeMethods.IsWindow(found))
        {
            _cachedTray = found;
            return _cachedTray;
        }

        return IntPtr.Zero;
    }

    private void UpdateWindowBounds()
    {
        var win = _window;
        if (win == null) return;
        var source = PresentationSource.FromVisual(win);
        if (source?.CompositionTarget == null) return;
        double scaleX = source.CompositionTarget.TransformToDevice.M11;
        double scaleY = source.CompositionTarget.TransformToDevice.M22;

        var screen = LinkedProfile.ScreenRef;
        var bounds = screen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

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
        bool isTargetChanged = (targetHwnd != _lastRenderedTargetHwnd);
        // 全ウィンドウ列挙は通常時 500ms 間引きだが、ウィンドウ切り替え時は即座にスキャンしてチラつきを防止
        bool needsSpecialWindowsScan = !isMoving && hasVisibleDimmer &&
            (isTargetChanged || DateTime.UtcNow - _lastSpecialWindowsScanUtc >= TimeSpan.FromMilliseconds(500));

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
                    if (hwnd == _myHandle || hwnd == targetHwnd) return true;
                    if (!FocusDimmerNativeMethods.IsWindowVisible(hwnd)) return true;
                    if (FocusDimmerNativeMethods.IsIconic(hwnd) || FocusDimmerNativeMethods.IsWindowCloaked(hwnd)) return true;

                    // 自プロセスのオーバーレイやHUDウィンドウは穴あけ対象から除外（干渉防止）
                    FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                    if (windowPid == (uint)Environment.ProcessId) return true;

                    // 先にウィンドウサイズをチェック。非表示用ダミーや極小ウィンドウはWin32判定前に早期スキップ
                    if (!FocusDimmerNativeMethods.GetWindowRect(hwnd, out var r)) return true;
                    if (r.Right - r.Left <= 20 || r.Bottom - r.Top <= 20) return true;

                    if (IsTaskbarWindow(hwnd))
                    {
                        // タスクバーは AddTaskbarHoles で直接処理するためスキップ
                        return true;
                    }

                    bool shouldAdd = false;

                    if (LinkedProfile.DimDesktopOnly && !forceNoHoles)
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
                        if (LinkedProfile.UseTightFrame && FocusDimmerNativeMethods.GetTightWindowRect(hwnd, out var tightR))
                        {
                            r = tightR;
                        }

                        if (r.Right - r.Left > 20 && r.Bottom - r.Top > 20)
                        {
                            specialWindows.Add(r);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
        }

        int currentRevision = OverlayRegionRegistry.Revision;
        if (!needsSpecialWindowsScan && _hasRenderedHoles &&
            targetHwnd == _lastRenderedTargetHwnd &&
            currentRect.Equals(_lastRenderedTargetRect) &&
            forceNoHoles == _lastRenderedForceNoHoles &&
            _lastOverlayRevision == currentRevision)
        {
            return;
        }

        _lastRenderedTargetHwnd = targetHwnd;
        _lastRenderedTargetRect = currentRect;
        _lastRenderedForceNoHoles = forceNoHoles;
        _lastOverlayRevision = currentRevision;
        _hasRenderedHoles = true;

        if (_window == null || _finalGeo == null) return;

        var source = PresentationSource.FromVisual(_window);
        if (source?.CompositionTarget == null) return;
        double scaleX = source.CompositionTarget.TransformToDevice.M11;
        double scaleY = source.CompositionTarget.TransformToDevice.M22;

        var newHolesGroup = new GeometryGroup { FillRule = FillRule.Nonzero };

        if (hasVisibleDimmer)
        {
            if (!forceNoHoles && targetHwnd != IntPtr.Zero)
            {
                AddHoleToGroup(newHolesGroup, currentRect, LinkedProfile.Margin, scaleX, scaleY);
            }

            // 自プロセスの登録されたオーバーレイ（OmniGlance等）の表示領域をくり抜き（常に明るく、チラつきを防止）
            var brightRegions = OverlayRegionRegistry.GetAlwaysBrightRegions();
            foreach (var rect in brightRegions)
            {
                var r = new FocusDimmerNativeMethods.RECT
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Right = rect.Right,
                    Bottom = rect.Bottom
                };
                AddHoleToGroup(newHolesGroup, r, 0, scaleX, scaleY);
            }

            // 他の明るいポップアップ・メニュー等のウィンドウを維持
            foreach (var r in _reusableSpecialWindows)
            {
                AddHoleToGroup(newHolesGroup, r, 0, scaleX, scaleY);
            }
        }

        // GeometryGroup をフリーズしてアトミックに差し替え（中間状態のチラつきを完全に根絶）
        newHolesGroup.Freeze();
        _finalGeo.Geometry2 = newHolesGroup;
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

    private void AddHoleToGroup(GeometryGroup group, FocusDimmerNativeMethods.RECT r, double margin, double scaleX, double scaleY)
    {
        var screen = LinkedProfile.ScreenRef;
        if (screen == null) return;

        double width = (r.Right - r.Left);
        double height = (r.Bottom - r.Top);
        if (width <= 1 || height <= 1) return;

        double physLeft = r.Left - screen.Bounds.Left;
        double physTop = r.Top - screen.Bounds.Top;

        double left = (physLeft + 1) / scaleX - margin;
        double top = (physTop + 1) / scaleY - margin;
        double w = width / scaleX + (margin * 2);
        double h = height / scaleY + (margin * 2);

        if (left + w > 0 && top + h > 0)
        {
            var rGeo = new RectangleGeometry(new Rect(left, top, w, h));
            rGeo.Freeze();
            group.Children.Add(rGeo);
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
