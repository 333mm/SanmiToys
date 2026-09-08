using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SanmiToys.Core;
using SanmiToys.Core.Helpers;
using SanmiToys.Core.Services;
using SanmiToys.Modules.OmniGlance.Models;
using SanmiToys.Modules.OmniGlance.Services;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SanmiToys.Modules.OmniGlance.Views;

public partial class OmniIslandWindow : Window
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;
    private const int DWMWA_MICA_EFFECT = 1029;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly Func<OmniGlanceSettings> _getSettings;
    private readonly Action<OmniGlanceSettings> _saveSettings;
    private readonly PerformanceMonitorService _perfService;
    private readonly BatteryMonitorService _batteryService;
    private readonly CalendarSyncService? _calendarService;
    private readonly Action<string>? _navigateAction;

    private readonly List<WorldClockInfo> _additionalClocks = new();
    private DateTime _currentCalendarMonth = DateTime.Today;
    private DateTime _selectedCalendarDate = DateTime.Today;
    private readonly ObservableCollection<CalendarDayCell> _calendarDayCells = new();

    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _alertDismissTimer;
    private bool _isExpanded;
    private bool _isAlertActive;
    private Point _dragStartPos;
    private bool _isPotentialDrag;
    private bool _isDragging;
    private Storyboard? _pulseStoryboard;
    private DispatcherTimer? _hoverCollapseTimer;

    private double _compactAnchorTop = -1;
    private double _compactAnchorBottom = -1;
    private double _compactAnchorLeft = -1;
    private double _compactAnchorRight = -1;
    private double _compactAnchorCenterX = -1;
    private double _compactAnchorCenterY = -1;
    private int _transitionGeneration;
    private bool IsVerticalMode => _getSettings().Orientation == IslandOrientation.Vertical;

    private IntPtr _mouseHook = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private DateTime _expandedTime = DateTime.MinValue;
    private System.Windows.Controls.ContextMenu? _activeContextMenu;
    private bool _isPinned;

    private enum IslandState
    {
        Compact,
        Expanded,
        Alert
    }

    private IslandState _currentState = IslandState.Compact;

    public OmniIslandWindow(
        Func<OmniGlanceSettings> getSettings,
        Action<OmniGlanceSettings> saveSettings,
        PerformanceMonitorService perfService,
        BatteryMonitorService batteryService,
        CalendarSyncService? calendarService = null,
        Action<string>? navigateAction = null)
    {
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _perfService = perfService;
        _batteryService = batteryService;
        _calendarService = calendarService;
        _navigateAction = navigateAction;

        InitializeComponent();

        _pulseStoryboard = TryFindResource("PulseGlowStoryboard") as Storyboard;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowStyle();
        ApplySettings();

        // 初期サイズをアニメーションなしで即時確定（起動時の見切れ・チラつきを完全防止）
        if (IsVerticalMode)
        {
            IslandPill.Width = 38;
            IslandPill.Height = CalculateCompactHeight();
        }
        else
        {
            IslandPill.Height = 38;
            IslandPill.Width = CalculateCompactWidth();
        }

        BatteryCompactItemsControl.ItemsSource = _batteryService.Devices;
        BatteryVerticalItemsControl.ItemsSource = _batteryService.Devices;
        ExpandedDeviceItemsControl.ItemsSource = _batteryService.Devices;

        _perfService.PerformanceInfo.PropertyChanged += OnPerformanceChanged;
        _batteryService.LowBatteryAlertTriggered += OnLowBatteryAlert;
        DispatcherTimer? collectionDebounceTimer = null;
        _batteryService.Devices.CollectionChanged += (s, args) =>
        {
            if (collectionDebounceTimer == null)
            {
                collectionDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                collectionDebounceTimer.Tick += (sender, e) =>
                {
                    collectionDebounceTimer.Stop();
                    UpdateSeparatorVisibility();
                    if (_currentState == IslandState.Compact)
                    {
                        UpdateCompactSizeSmoothly();
                    }
                };
            }
            collectionDebounceTimer.Stop();
            collectionDebounceTimer.Start();
        };

        CalendarDaysItemsControl.ItemsSource = _calendarDayCells;
        if (_calendarService != null)
        {
            _calendarService.CalendarUpdated += OnCalendarServiceUpdated;
        }
        PopulateCalendar(_currentCalendarMonth);
        UpdateSelectedDateEvents(_selectedCalendarDate);

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();

        // Windows の追加のタイムゾーン時計を読み込み
        _additionalClocks.Clear();
        _additionalClocks.AddRange(AdditionalClocksService.LoadConfiguredClocks());
        if (_additionalClocks.Count > 0)
        {
            AdditionalClocksItemsControl.ItemsSource = _additionalClocks;
            AdditionalClocksItemsControl.Visibility = Visibility.Visible;
        }
        else
        {
            AdditionalClocksItemsControl.Visibility = Visibility.Collapsed;
        }

        UpdateClock();
        UpdateCalendarIndicator();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        OverlayRegionRegistry.UnregisterWindow("OmniGlance");
        OverlayRegionRegistry.Unregister("OmniGlance");

        _clockTimer?.Stop();
        _alertDismissTimer?.Stop();
        _hoverCollapseTimer?.Stop();
        UninstallMouseHook();
        _perfService.PerformanceInfo.PropertyChanged -= OnPerformanceChanged;
        _batteryService.LowBatteryAlertTriggered -= OnLowBatteryAlert;
        if (_calendarService != null)
        {
            _calendarService.CalendarUpdated -= OnCalendarServiceUpdated;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // FocusDimmer 背面配置用にウィンドウハンドルを登録
            OverlayRegionRegistry.RegisterWindow("OmniGlance", hwnd);

            // Windows 11 DWM システムバックドロップを確実に無効化し、グレー背景化を根絶
            try
            {
                if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621)
                {
                    int none = DWMSBT_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
                }
                else if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000)
                {
                    int zero = 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref zero, sizeof(int));
                }
            }
            catch { }
        }

        ApplyWindowStyle();
    }

    public void ApplyWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

        var settings = _getSettings();
        if (settings.IsClickThrough && !_isExpanded)
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        }

        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    public void ApplySettings()
    {
        var settings = _getSettings();

        // 後方互換: 旧PositionModeが設定されていて新Orientationがデフォルトの場合はマイグレーション
        MigrateLegacyPositionIfNeeded(settings);

        // 1. 不透明度 (背景のみに適用し、テキストやアイコン等のコンテンツは不透明度100%を維持)
        IslandPill.Opacity = 1.0;
        // 不透明度0%時でもWindowsのレイヤードウィンドウ当たり判定が失われないよう、alphaの最小値を1に制限
        byte alpha = (byte)Math.Clamp((int)(settings.Opacity * 255), 1, 255);
        IslandPill.Background = new SolidColorBrush(Color.FromArgb(alpha, 11, 12, 14));
        IslandPill.BorderThickness = new Thickness(0);
        IslandPill.BorderBrush = Brushes.Transparent;

        // 2. 表示倍率 (スケール) - 高品質ベクター
        double scale = Math.Clamp(settings.IslandScale, 0.7, 1.5);
        WindowScaleTransform.ScaleX = scale;
        WindowScaleTransform.ScaleY = scale;

        // 3. 表示項目 (横表示用 & 縦表示用)
        bool isVertical = IsVerticalMode;
        CompactStackPanel.Visibility = !isVertical ? Visibility.Visible : Visibility.Collapsed;
        CompactVerticalPanel.Visibility = isVertical ? Visibility.Visible : Visibility.Collapsed;
        AlertHorizontalPanel.Visibility = !isVertical ? Visibility.Visible : Visibility.Collapsed;
        AlertVerticalPanel.Visibility = isVertical ? Visibility.Visible : Visibility.Collapsed;

        ClockCompactSection.Visibility = settings.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        ClockVerticalSection.Visibility = settings.ShowClock ? Visibility.Visible : Visibility.Collapsed;

        bool hasAnyPerfItem = settings.ShowCpuUsage || settings.ShowGpuUsage || settings.ShowRamUsage || settings.ShowPowerUsage;
        PerfCompactSection.Visibility = (settings.ShowPerformance && hasAnyPerfItem) ? Visibility.Visible : Visibility.Collapsed;
        PerfVerticalSection.Visibility = (settings.ShowPerformance && hasAnyPerfItem) ? Visibility.Visible : Visibility.Collapsed;

        CpuCompactItem.Visibility = (settings.ShowPerformance && settings.ShowCpuUsage) ? Visibility.Visible : Visibility.Collapsed;
        GpuCompactItem.Visibility = (settings.ShowPerformance && settings.ShowGpuUsage) ? Visibility.Visible : Visibility.Collapsed;
        RamCompactItem.Visibility = (settings.ShowPerformance && settings.ShowRamUsage) ? Visibility.Visible : Visibility.Collapsed;
        PowerCompactItem.Visibility = (settings.ShowPerformance && settings.ShowPowerUsage) ? Visibility.Visible : Visibility.Collapsed;

        CpuVerticalItem.Visibility = (settings.ShowPerformance && settings.ShowCpuUsage) ? Visibility.Visible : Visibility.Collapsed;
        GpuVerticalItem.Visibility = (settings.ShowPerformance && settings.ShowGpuUsage) ? Visibility.Visible : Visibility.Collapsed;
        RamVerticalItem.Visibility = (settings.ShowPerformance && settings.ShowRamUsage) ? Visibility.Visible : Visibility.Collapsed;
        PowerVerticalItem.Visibility = (settings.ShowPerformance && settings.ShowPowerUsage) ? Visibility.Visible : Visibility.Collapsed;

        CpuExpandedSection.Visibility = (settings.ShowPerformance && settings.ShowCpuUsage) ? Visibility.Visible : Visibility.Collapsed;
        GpuExpandedSection.Visibility = (settings.ShowPerformance && settings.ShowGpuUsage) ? Visibility.Visible : Visibility.Collapsed;
        RamExpandedSection.Visibility = (settings.ShowPerformance && settings.ShowRamUsage) ? Visibility.Visible : Visibility.Collapsed;
        PowerExpandedSection.Visibility = (settings.ShowPerformance && settings.ShowPowerUsage) ? Visibility.Visible : Visibility.Collapsed;

        BatteryCompactSection.Visibility = settings.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
        BatteryVerticalSection.Visibility = settings.ShowBattery ? Visibility.Visible : Visibility.Collapsed;

        // パフォーマンスとバッテリー表示間の薄い罫線の表示切替
        UpdateSeparatorVisibility();

        // バッテリーデバイスの表示フィルタ更新 (設定で無効化されたデバイスをスマートに除外)
        var batteryView = System.Windows.Data.CollectionViewSource.GetDefaultView(_batteryService.Devices);
        if (batteryView != null)
        {
            batteryView.Filter = obj =>
            {
                if (obj is DeviceBatteryInfo dev)
                {
                    bool isDis = settings.DisabledDeviceIds.Contains(dev.Id) || settings.DisabledDeviceIds.Contains(dev.Name);
                    return !isDis;
                }
                return true;
            };
            batteryView.Refresh();
        }

        // 4. 位置ロック
        IslandPill.Cursor = settings.IsPositionLocked ? Cursors.Arrow : Cursors.Hand;

        ApplyPosition();
        ApplyWindowStyle();

        // 現在のステートに応じたグリッドの確実な可視化保証（消失事故のセルフヒーリング）
        switch (_currentState)
        {
            case IslandState.Compact:
                CompactGrid.BeginAnimation(UIElement.OpacityProperty, null);
                CompactGrid.Visibility = Visibility.Visible;
                CompactGrid.Opacity = 1.0;
                ExpandedGrid.Visibility = Visibility.Collapsed;
                AlertGrid.Visibility = Visibility.Collapsed;
                break;
            case IslandState.Expanded:
                ExpandedGrid.BeginAnimation(UIElement.OpacityProperty, null);
                ExpandedGrid.Visibility = Visibility.Visible;
                ExpandedGrid.Opacity = 1.0;
                CompactGrid.Visibility = Visibility.Collapsed;
                AlertGrid.Visibility = Visibility.Collapsed;
                break;
            case IslandState.Alert:
                AlertGrid.BeginAnimation(UIElement.OpacityProperty, null);
                AlertGrid.Visibility = Visibility.Visible;
                AlertGrid.Opacity = 1.0;
                CompactGrid.Visibility = Visibility.Collapsed;
                ExpandedGrid.Visibility = Visibility.Collapsed;
                break;
        }

        if (_currentState == IslandState.Compact)
        {
            UpdateCompactSizeSmoothly();
        }
        else if (_currentState == IslandState.Alert)
        {
            TransitionToState(IslandState.Alert);
        }
    }

    private void UpdateSeparatorVisibility()
    {
        var settings = _getSettings();
        bool hasClock = settings.ShowClock;
        bool hasAnyPerfItem = settings.ShowCpuUsage || settings.ShowGpuUsage || settings.ShowRamUsage || settings.ShowPowerUsage;
        bool hasPerf = settings.ShowPerformance && hasAnyPerfItem;
        bool hasBattery = settings.ShowBattery && _batteryService.Devices.Any(d =>
            !settings.DisabledDeviceIds.Contains(d.Id) && !settings.DisabledDeviceIds.Contains(d.Name));

        bool showPerfSep = (hasClock && hasPerf);
        bool showBatterySep = ((hasPerf && hasBattery) || (!hasPerf && hasClock && hasBattery));

        ClockPerfSeparator.Visibility = showPerfSep ? Visibility.Visible : Visibility.Collapsed;
        PerfBatterySeparator.Visibility = showBatterySep ? Visibility.Visible : Visibility.Collapsed;

        ClockPerfVerticalSeparator.Visibility = showPerfSep ? Visibility.Visible : Visibility.Collapsed;
        PerfBatteryVerticalSeparator.Visibility = showBatterySep ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>旧 PositionMode が残っている場合のみ新体系へ移行する（1回限り）</summary>
#pragma warning disable CS0618
    private void MigrateLegacyPositionIfNeeded(OmniGlanceSettings settings)
    {
        if (settings.HasMigratedToSlots) return;

        // 旧 PositionMode をチェックしてマッピング
        switch (settings.PositionMode)
        {
            case IslandPositionMode.TopLeft:
                settings.Orientation = IslandOrientation.Horizontal;
                settings.PositionSlot = IslandPositionSlot.StartStart;
                break;
            case IslandPositionMode.TopCenter:
                settings.Orientation = IslandOrientation.Horizontal;
                settings.PositionSlot = IslandPositionSlot.CenterStart;
                break;
            case IslandPositionMode.TopRight:
                settings.Orientation = IslandOrientation.Horizontal;
                settings.PositionSlot = IslandPositionSlot.EndStart;
                break;
            case IslandPositionMode.BottomLeft:
                settings.Orientation = IslandOrientation.Horizontal;
                settings.PositionSlot = IslandPositionSlot.StartEnd;
                break;
            case IslandPositionMode.BottomCenter:
                settings.Orientation = IslandOrientation.Horizontal;
                settings.PositionSlot = IslandPositionSlot.CenterEnd;
                break;
            case IslandPositionMode.BottomRight:
                settings.Orientation = IslandOrientation.Horizontal;
                settings.PositionSlot = IslandPositionSlot.EndEnd;
                break;
            case IslandPositionMode.LeftCenter:
                settings.Orientation = IslandOrientation.Vertical;
                settings.PositionSlot = IslandPositionSlot.StartCenter;
                break;
            case IslandPositionMode.RightCenter:
                settings.Orientation = IslandOrientation.Vertical;
                settings.PositionSlot = IslandPositionSlot.EndCenter;
                break;
            default:
                break;
        }
        settings.HasMigratedToSlots = true;
        _saveSettings(settings);
    }
#pragma warning restore CS0618

    public void ApplyPosition()
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        var settings = _getSettings();
        double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;

        bool isVertical = IsVerticalMode;
        double compactPillW = isVertical ? 38 : CalculateCompactWidth();
        double compactPillH = isVertical ? CalculateCompactHeight() : 38;
        double windowW = (compactPillW + 32) * scale;
        double windowH = (compactPillH + 32) * scale;

        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        double workTop = SystemParameters.WorkArea.Top;
        double workBottom = SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height;

        const double vGap = 2.0;
        const double hGap = 4.0;

        double leftEdge = hGap - (16 * scale);
        double rightEdge = screenW - windowW + (16 * scale) - hGap;
        double topEdge = workTop + vGap - (16 * scale);
        double bottomEdge = workBottom - vGap - (windowH - 16 * scale);
        double centerX = (screenW - windowW) / 2.0;
        double centerY = (screenH - windowH) / 2.0;

        double targetLeft, targetTop;

        if (settings.IsCustomPosition && settings.CustomLeft >= 0 && settings.CustomTop >= 0)
        {
            targetLeft = Math.Clamp(settings.CustomLeft, 0, Math.Max(0, screenW - windowW));
            targetTop = Math.Clamp(settings.CustomTop, workTop, Math.Max(workTop, workBottom - windowH));
        }
        else if (isVertical)
        {
            // 縦モード: 左右 → Slot の先頭文字(Start=左/End=右)、上下 → Slot の後半(Start=上/Center=中/End=下)
            bool isRight = settings.PositionSlot is IslandPositionSlot.EndStart or IslandPositionSlot.EndCenter or IslandPositionSlot.EndEnd;
            targetLeft = isRight ? rightEdge : leftEdge;

            targetTop = settings.PositionSlot switch
            {
                IslandPositionSlot.StartStart or IslandPositionSlot.EndStart => topEdge,
                IslandPositionSlot.StartCenter or IslandPositionSlot.EndCenter => centerY,
                IslandPositionSlot.StartEnd or IslandPositionSlot.EndEnd => bottomEdge,
                _ => topEdge
            };
        }
        else
        {
            // 横モード: 左中右 → Slot の先頭(Start/Center/End)、上下 → Slot の後半(Start=上/End=下)
            targetLeft = settings.PositionSlot switch
            {
                IslandPositionSlot.StartStart or IslandPositionSlot.StartEnd => leftEdge,
                IslandPositionSlot.CenterStart or IslandPositionSlot.CenterEnd => centerX,
                IslandPositionSlot.EndStart or IslandPositionSlot.EndEnd => rightEdge,
                _ => centerX
            };

            bool isBottom = settings.PositionSlot is IslandPositionSlot.StartEnd
                or IslandPositionSlot.CenterEnd or IslandPositionSlot.EndEnd;
            targetTop = isBottom ? bottomEdge : topEdge;
        }

        Left = targetLeft;
        Top = targetTop;

        // アンカー更新
        _compactAnchorLeft = Left;
        _compactAnchorRight = Left + windowW;
        _compactAnchorTop = Top;
        _compactAnchorBottom = Top + windowH;
        _compactAnchorCenterX = Left + windowW / 2.0;
        _compactAnchorCenterY = Top + windowH / 2.0;
    }

    /// <summary>展開後のウィンドウRect(Left,Top,Width,Height)をSlot基準で計算しコンパクト時と同じ画面端マージンを維持して返す</summary>
    private (double Left, double Top, double Width, double Height) GetExpandedWindowRect(
        OmniGlanceSettings settings, double scale)
    {
        bool isVertical = settings.Orientation == IslandOrientation.Vertical;

        // 展開サイズ (見切れ防止のため縦横共通で 540×260)
        double pillW = 540;
        double pillH = 260;
        double winW = (pillW + 32) * scale;
        double winH = (pillH + 32) * scale;

        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        double workTop = SystemParameters.WorkArea.Top;
        double workBottom = SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height;
        const double vGap = 2.0;
        const double hGap = 4.0;

        // コンパクト時とミリ単位で完全一致する画面端オフセット
        double leftEdge = hGap - (16 * scale);
        double rightEdge = screenW - winW + (16 * scale) - hGap;
        double topEdge = workTop + vGap - (16 * scale);
        double bottomEdge = workBottom - vGap - (winH - 16 * scale);
        double centerX = (screenW - winW) / 2.0;
        double centerY = workTop + (workBottom - workTop - winH) / 2.0;

        double ancLeft = _compactAnchorLeft >= 0 ? _compactAnchorLeft : Left;
        double ancRight = _compactAnchorRight >= 0 ? _compactAnchorRight : Left + winW;
        double ancTop = _compactAnchorTop >= 0 ? _compactAnchorTop : Top;
        double ancBottom = _compactAnchorBottom >= 0 ? _compactAnchorBottom : Top + winH;
        double ancCenterX = _compactAnchorCenterX >= 0 ? _compactAnchorCenterX : Left + winW / 2.0;
        double ancCenterY = _compactAnchorCenterY >= 0 ? _compactAnchorCenterY : Top + winH / 2.0;

        double targetLeft, targetTop;

        if (settings.IsCustomPosition)
        {
            if (isVertical)
            {
                bool isRight = ancLeft > screenW / 2.0;
                targetLeft = isRight ? rightEdge : leftEdge;
                double relCenter = ancCenterY / screenH;
                if (relCenter < 0.35) targetTop = topEdge;
                else if (relCenter > 0.65) targetTop = bottomEdge;
                else targetTop = centerY;
            }
            else
            {
                double relCenterX = ancCenterX / screenW;
                if (relCenterX < 0.35) targetLeft = leftEdge;
                else if (relCenterX > 0.65) targetLeft = rightEdge;
                else targetLeft = centerX;

                bool isBottom = ancCenterY > screenH / 2.0;
                targetTop = isBottom ? bottomEdge : topEdge;
            }
        }
        else if (isVertical)
        {
            bool isRight = settings.PositionSlot is IslandPositionSlot.EndStart or IslandPositionSlot.EndCenter or IslandPositionSlot.EndEnd;
            targetLeft = isRight ? rightEdge : leftEdge;

            targetTop = settings.PositionSlot switch
            {
                IslandPositionSlot.StartStart or IslandPositionSlot.EndStart => topEdge,
                IslandPositionSlot.StartCenter or IslandPositionSlot.EndCenter => centerY,
                IslandPositionSlot.StartEnd or IslandPositionSlot.EndEnd => bottomEdge,
                _ => topEdge
            };
        }
        else
        {
            targetLeft = settings.PositionSlot switch
            {
                IslandPositionSlot.StartStart or IslandPositionSlot.StartEnd => leftEdge,
                IslandPositionSlot.CenterStart or IslandPositionSlot.CenterEnd => centerX,
                IslandPositionSlot.EndStart or IslandPositionSlot.EndEnd => rightEdge,
                _ => centerX
            };

            bool isBottom = settings.PositionSlot is IslandPositionSlot.StartEnd
                or IslandPositionSlot.CenterEnd or IslandPositionSlot.EndEnd;
            targetTop = isBottom ? bottomEdge : topEdge;
        }

        targetLeft = Math.Clamp(targetLeft, leftEdge, Math.Max(leftEdge, rightEdge));
        targetTop = Math.Clamp(targetTop, topEdge, Math.Max(topEdge, bottomEdge));

        return (targetLeft, targetTop, winW, winH);
    }

    /// <summary>アラート表示時のウィンドウRectを計算し、コンパクト時と同じ位置関係を保って返す</summary>
    private (double Left, double Top, double Width, double Height) GetAlertWindowRect(
        OmniGlanceSettings settings, double scale)
    {
        bool isVertical = settings.Orientation == IslandOrientation.Vertical;
        double pillW = isVertical ? 38 : 420;
        double pillH = isVertical ? 120 : 44;
        double winW = (pillW + 32) * scale;
        double winH = (pillH + 32) * scale;

        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        double workTop = SystemParameters.WorkArea.Top;
        double workBottom = SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height;
        const double vGap = 2.0;
        const double hGap = 4.0;

        double leftEdge = hGap - (16 * scale);
        double rightEdge = screenW - winW + (16 * scale) - hGap;
        double topEdge = workTop + vGap - (16 * scale);
        double bottomEdge = workBottom - vGap - (winH - 16 * scale);
        double centerX = (screenW - winW) / 2.0;
        double centerY = workTop + (workBottom - workTop - winH) / 2.0;

        double ancLeft = _compactAnchorLeft >= 0 ? _compactAnchorLeft : Left;
        double ancRight = _compactAnchorRight >= 0 ? _compactAnchorRight : Left + winW;
        double ancTop = _compactAnchorTop >= 0 ? _compactAnchorTop : Top;
        double ancBottom = _compactAnchorBottom >= 0 ? _compactAnchorBottom : Top + winH;
        double ancCenterX = _compactAnchorCenterX >= 0 ? _compactAnchorCenterX : Left + winW / 2.0;
        double ancCenterY = _compactAnchorCenterY >= 0 ? _compactAnchorCenterY : Top + winH / 2.0;

        double targetLeft, targetTop;

        if (settings.IsCustomPosition)
        {
            if (isVertical)
            {
                bool isRight = ancLeft > screenW / 2.0;
                targetLeft = isRight ? ancRight - winW : ancLeft;
                double relCenter = ancCenterY / screenH;
                if (relCenter < 0.35) targetTop = ancTop;
                else if (relCenter > 0.65) targetTop = ancBottom - winH;
                else targetTop = ancCenterY - winH / 2.0;
            }
            else
            {
                double relCenterX = ancCenterX / screenW;
                if (relCenterX < 0.35) targetLeft = ancLeft;
                else if (relCenterX > 0.65) targetLeft = ancRight - winW;
                else targetLeft = ancCenterX - winW / 2.0;

                bool isBottom = ancCenterY > screenH / 2.0;
                targetTop = isBottom ? ancBottom - winH : ancTop;
            }
        }
        else if (isVertical)
        {
            bool isRight = settings.PositionSlot is IslandPositionSlot.EndStart or IslandPositionSlot.EndCenter or IslandPositionSlot.EndEnd;
            targetLeft = isRight ? rightEdge : leftEdge;
            targetTop = settings.PositionSlot switch
            {
                IslandPositionSlot.StartStart or IslandPositionSlot.EndStart => topEdge,
                IslandPositionSlot.StartCenter or IslandPositionSlot.EndCenter => centerY,
                IslandPositionSlot.StartEnd or IslandPositionSlot.EndEnd => bottomEdge,
                _ => topEdge
            };
        }
        else
        {
            targetLeft = settings.PositionSlot switch
            {
                IslandPositionSlot.StartStart or IslandPositionSlot.StartEnd => leftEdge,
                IslandPositionSlot.CenterStart or IslandPositionSlot.CenterEnd => centerX,
                IslandPositionSlot.EndStart or IslandPositionSlot.EndEnd => rightEdge,
                _ => centerX
            };
            bool isBottom = settings.PositionSlot is IslandPositionSlot.StartEnd or IslandPositionSlot.CenterEnd or IslandPositionSlot.EndEnd;
            targetTop = isBottom ? bottomEdge : topEdge;
        }

        targetLeft = Math.Clamp(targetLeft, leftEdge, Math.Max(leftEdge, rightEdge));
        targetTop = Math.Clamp(targetTop, topEdge, Math.Max(topEdge, bottomEdge));

        return (targetLeft, targetTop, winW, winH);
    }

    private double CalculateCompactWidth()
    {
        bool wasGridCollapsed = CompactGrid.Visibility != Visibility.Visible;
        if (wasGridCollapsed)
        {
            CompactGrid.Visibility = Visibility.Visible;
        }
        bool wasPanelCollapsed = CompactStackPanel.Visibility != Visibility.Visible;
        if (wasPanelCollapsed)
        {
            CompactStackPanel.Visibility = Visibility.Visible;
        }

        CompactStackPanel.UpdateLayout();
        CompactGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double contentW = CompactStackPanel.DesiredSize.Width;

        if (wasPanelCollapsed)
        {
            CompactStackPanel.Visibility = Visibility.Collapsed;
        }
        if (wasGridCollapsed && _currentState != IslandState.Compact)
        {
            CompactGrid.Visibility = Visibility.Collapsed;
        }

        // 左右パディング(12*2=24)は CompactStackPanel の DesiredSize に含まれるため、サブピクセルバッファのみ加算
        double target = Math.Max(70, contentW + 2);
        return Math.Min(SystemParameters.PrimaryScreenWidth - 40, target);
    }

    private double CalculateCompactHeight()
    {
        bool wasGridCollapsed = CompactGrid.Visibility != Visibility.Visible;
        if (wasGridCollapsed)
        {
            CompactGrid.Visibility = Visibility.Visible;
        }
        bool wasPanelCollapsed = CompactVerticalPanel.Visibility != Visibility.Visible;
        if (wasPanelCollapsed)
        {
            CompactVerticalPanel.Visibility = Visibility.Visible;
        }

        CompactVerticalPanel.UpdateLayout();
        CompactGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double contentH = CompactVerticalPanel.DesiredSize.Height;

        if (wasPanelCollapsed)
        {
            CompactVerticalPanel.Visibility = Visibility.Collapsed;
        }
        if (wasGridCollapsed && _currentState != IslandState.Compact)
        {
            CompactGrid.Visibility = Visibility.Collapsed;
        }

        // 上下パディング(10*2=20)は CompactVerticalPanel の DesiredSize に含まれるため、サブピクセルバッファのみ加算
        double target = Math.Max(60, contentH + 2);
        return Math.Min(SystemParameters.PrimaryScreenHeight - 40, target);
    }

    private void UpdateCompactSizeSmoothly()
    {
        if (_currentState != IslandState.Compact) return;

        // 万が一のフェード競合等による非表示化・透明化を防ぐ自己復帰
        if (CompactGrid.Visibility != Visibility.Visible || CompactGrid.Opacity < 0.99)
        {
            CompactGrid.BeginAnimation(UIElement.OpacityProperty, null);
            CompactGrid.Visibility = Visibility.Visible;
            CompactGrid.Opacity = 1.0;
        }

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        var settings = _getSettings();
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;

        double workTop = SystemParameters.WorkArea.Top;
        double workBottom = SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height;

        if (IsVerticalMode)
        {
            double targetH = CalculateCompactHeight();
            double currentH = IslandPill.ActualHeight > 0 ? IslandPill.ActualHeight : targetH;

            // 幅は 38 固定
            IslandPill.BeginAnimation(WidthProperty, null);
            IslandPill.Width = 38;

            double newWindowHeight = (targetH + 32) * scale;
            double targetTop;

            if (settings.IsCustomPosition && _compactAnchorTop >= 0)
            {
                targetTop = _compactAnchorTop;
            }
            else
            {
                targetTop = settings.PositionSlot switch
                {
                    IslandPositionSlot.StartStart or IslandPositionSlot.EndStart => workTop + 2.0 - (16 * scale),
                    IslandPositionSlot.StartEnd or IslandPositionSlot.EndEnd => workBottom - 2.0 - (newWindowHeight - 16 * scale),
                    _ => (screenHeight - newWindowHeight) / 2.0
                };
            }
            targetTop = Math.Clamp(targetTop, workTop + 2.0 - (16 * scale), Math.Max(workTop + 2.0 - (16 * scale), workBottom - 2.0 - (newWindowHeight - 16 * scale)));

            if (Math.Abs(currentH - targetH) >= 1.0)
            {
                var anim = new DoubleAnimation(currentH, targetH, duration) { EasingFunction = ease };
                IslandPill.BeginAnimation(HeightProperty, anim);

                var topAnim = new DoubleAnimation(Top, targetTop, duration) { EasingFunction = ease };
                BeginAnimation(TopProperty, topAnim);
            }

            double leftEdgePosition = 4.0 - (16 * scale);
            double newWindowWidth = (38 + 32) * scale;
            double rightEdgePosition = screenWidth - newWindowWidth + (16 * scale) - 4.0;
            
            double targetLeft;
            if (settings.IsCustomPosition && _compactAnchorLeft >= 0)
            {
                targetLeft = _compactAnchorLeft;
            }
            else
            {
                bool isRight = settings.PositionSlot is IslandPositionSlot.EndStart or IslandPositionSlot.EndCenter or IslandPositionSlot.EndEnd;
                targetLeft = isRight ? rightEdgePosition : leftEdgePosition;
            }
            targetLeft = Math.Clamp(targetLeft, leftEdgePosition, Math.Max(leftEdgePosition, rightEdgePosition));

            if (Math.Abs(Left - targetLeft) >= 1.0)
            {
                var leftAnim = new DoubleAnimation(Left, targetLeft, duration) { EasingFunction = ease };
                BeginAnimation(LeftProperty, leftAnim);
            }

            _compactAnchorLeft = targetLeft;
            _compactAnchorRight = targetLeft + newWindowWidth;
            _compactAnchorTop = targetTop;
            _compactAnchorBottom = targetTop + newWindowHeight;
            _compactAnchorCenterX = targetLeft + newWindowWidth / 2.0;
            _compactAnchorCenterY = targetTop + newWindowHeight / 2.0;
        }
        else
        {
            double targetW = CalculateCompactWidth();
            double currentW = IslandPill.ActualWidth > 0 ? IslandPill.ActualWidth : targetW;

            // 高さは 38 固定
            IslandPill.BeginAnimation(HeightProperty, null);
            IslandPill.Height = 38;

            if (Math.Abs(currentW - targetW) >= 1.0)
            {
                var anim = new DoubleAnimation(currentW, targetW, duration) { EasingFunction = ease };
                IslandPill.BeginAnimation(WidthProperty, anim);

                double newWindowWidth = (targetW + 32) * scale;
                double newWindowHeight = (38 + 32) * scale;
                double targetLeft;

                double leftEdgePosition = 4.0 - (16 * scale);
                double rightEdgePosition = screenWidth - newWindowWidth + (16 * scale) - 4.0;

                if (settings.IsCustomPosition)
                {
                    if (_compactAnchorCenterX < 0)
                    {
                        _compactAnchorCenterX = Left + (newWindowWidth / 2.0);
                    }
                    targetLeft = _compactAnchorCenterX - (newWindowWidth / 2.0);
                }
                else
                {
                    targetLeft = settings.PositionSlot switch
                    {
                        IslandPositionSlot.StartStart or IslandPositionSlot.StartEnd => leftEdgePosition,
                        IslandPositionSlot.EndStart or IslandPositionSlot.EndEnd => rightEdgePosition,
                        _ => (screenWidth - newWindowWidth) / 2.0
                    };
                    _compactAnchorCenterX = targetLeft + (newWindowWidth / 2.0);
                }

                targetLeft = Math.Clamp(targetLeft, leftEdgePosition, Math.Max(leftEdgePosition, rightEdgePosition));

                double topEdge = workTop + 2.0 - (16 * scale);
                double bottomEdge = workBottom - 2.0 - (newWindowHeight - 16 * scale);
                double targetTop;
                if (settings.IsCustomPosition && _compactAnchorTop >= 0)
                {
                    targetTop = _compactAnchorTop;
                }
                else
                {
                    bool isBottom = settings.PositionSlot is IslandPositionSlot.StartEnd
                        or IslandPositionSlot.CenterEnd or IslandPositionSlot.EndEnd;
                    targetTop = isBottom ? bottomEdge : topEdge;
                }
                targetTop = Math.Clamp(targetTop, topEdge, Math.Max(topEdge, bottomEdge));

                var leftAnim = new DoubleAnimation(Left, targetLeft, duration) { EasingFunction = ease };
                BeginAnimation(LeftProperty, leftAnim);

                if (Math.Abs(Top - targetTop) >= 1.0)
                {
                    var topAnim = new DoubleAnimation(Top, targetTop, duration) { EasingFunction = ease };
                    BeginAnimation(TopProperty, topAnim);
                }

                _compactAnchorLeft = targetLeft;
                _compactAnchorRight = targetLeft + newWindowWidth;
                _compactAnchorTop = targetTop;
                _compactAnchorBottom = targetTop + newWindowHeight;
                _compactAnchorCenterY = targetTop + newWindowHeight / 2.0;
            }
        }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        UpdateClock();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockCompactText.Text = now.ToString("HH:mm");
        DayCompactText.Text = now.ToString("ddd");

        ClockVerticalHourText.Text = now.ToString("HH");
        ClockVerticalMinText.Text = now.ToString("mm");
        DayVerticalText.Text = now.ToString("ddd");

        ExpandedTimeText.Text = now.ToString("HH:mm:ss");
        ExpandedDateText.Text = now.ToString("yyyy/MM/dd (ddd)");

        if (_additionalClocks.Count > 0)
        {
            AdditionalClocksService.UpdateClockTimes(_additionalClocks);
        }

        UpdateCalendarIndicator();

        // 定期自己復帰: コンパクト状態中にもかかわらず表示が非表示・透明になっている場合のフェイルセーフ
        if (_currentState == IslandState.Compact && (CompactGrid.Visibility != Visibility.Visible || CompactGrid.Opacity < 0.05))
        {
            CompactGrid.BeginAnimation(UIElement.OpacityProperty, null);
            CompactGrid.Visibility = Visibility.Visible;
            CompactGrid.Opacity = 1.0;
        }
    }

    private void OnClockClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("ms-clock:") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:Appsfolder\Microsoft.WindowsAlarms_8wekyb3d8bbwe!App") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void OnPerformanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var p = _perfService.PerformanceInfo;
            CpuCompactText.Text = p.CpuText;
            GpuCompactText.Text = p.GpuText;
            RamCompactText.Text = p.RamText;
            PowerCompactText.Text = p.PowerText;

            CpuVerticalText.Text = p.CpuText;
            GpuVerticalText.Text = p.GpuText;
            RamVerticalText.Text = p.RamText;
            PowerVerticalText.Text = p.PowerText;

            CpuProgressBar.Value = p.CpuUsage;
            CpuPercentLabel.Text = p.CpuText;

            GpuProgressBar.Value = p.GpuUsage;
            GpuPercentLabel.Text = p.GpuText;

            RamProgressBar.Value = p.RamUsage;
            RamPercentLabel.Text = p.RamText;
            RamDetailLabel.Text = $" ({p.UsedMemoryGb:F1} / {p.TotalMemoryGb:F1} GB)";

            PowerWattsLabel.Text = p.PowerText;
            PowerSourceLabel.Text = $" ({p.PowerSourceText})";
        });
    }

    private void OnLowBatteryAlert(DeviceBatteryInfo device)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var settings = _getSettings();
            if (!settings.EnablePulseAnimation && !settings.ShowBattery) return;

            _isAlertActive = true;
            var loc = LocalizationService.Instance;
            string title = device.IsCriticalBattery
                ? loc["OmniGlance_Alert_CriticalTitle"]
                : loc["OmniGlance_Alert_LowTitle"];
            string remainingFormat = loc["OmniGlance_Alert_RemainingFormat"];
            string message = string.Format(remainingFormat, device.Name, device.BatteryLevel);
            string levelText = device.BatteryText;

            // 横表示用更新
            AlertTitleText.Text = title;
            AlertMessageText.Text = message;
            AlertLevelText.Text = levelText;

            // 縦表示用更新
            AlertVerticalLevelText.Text = levelText;
            AlertVerticalBadgeText.Text = device.IsCriticalBattery ? "CRIT" : "LOW";

            // アラートカラー (Critical: 赤 / Low: オレンジ)
            Color alertColor = device.IsCriticalBattery ? Color.FromRgb(0xFF, 0x4D, 0x4F) : Color.FromRgb(0xFF, 0xA9, 0x40);
            var alertBrush = new SolidColorBrush(alertColor);
            var bgBrush = new SolidColorBrush(Color.FromArgb(0x33, alertColor.R, alertColor.G, alertColor.B));

            // 横表示用スタイル更新
            AlertIconBorder.Background = bgBrush;
            AlertIcon.Foreground = alertBrush;
            AlertLevelText.Foreground = alertBrush;

            // 縦表示用スタイル更新
            AlertVerticalIconBorder.Background = bgBrush;
            AlertVerticalIcon.Foreground = alertBrush;
            AlertVerticalLevelText.Foreground = alertBrush;
            AlertVerticalBadgeBorder.Background = bgBrush;
            AlertVerticalBadgeText.Foreground = alertBrush;

            // 縦表示用デバイスアイコン
            if (device.IsMouse)
            {
                AlertVerticalMousePath.Visibility = Visibility.Visible;
                AlertVerticalMousePath.Fill = alertBrush;
                AlertVerticalDeviceIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                AlertVerticalMousePath.Visibility = Visibility.Collapsed;
                AlertVerticalDeviceIcon.Visibility = Visibility.Visible;
                AlertVerticalDeviceIcon.Symbol = device.Symbol;
                AlertVerticalDeviceIcon.Foreground = alertBrush;
            }

            // ツールチップ設定
            string tooltipText = $"{device.Name}: {levelText} ({title})";
            AlertGrid.ToolTip = tooltipText;

            // 背景影(DropShadow)は黒のまま維持し透明背景への赤色漏れを防止
            GlowEffect.Color = Colors.Black;
            IslandPill.BorderThickness = new Thickness(1.5);
            IslandPill.BorderBrush = alertBrush;

            TransitionToState(IslandState.Alert);

            if (settings.EnablePulseAnimation)
            {
                _pulseStoryboard?.Begin(this, true);
            }

            _alertDismissTimer?.Stop();
            _alertDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            _alertDismissTimer.Tick += (s, args) =>
            {
                _alertDismissTimer.Stop();
                _isAlertActive = false;
                _pulseStoryboard?.Stop(this);
                IslandPill.BorderThickness = new Thickness(0);
                IslandPill.BorderBrush = Brushes.Transparent;
                if (!_isExpanded)
                {
                    TransitionToState(IslandState.Compact);
                }
            };
            _alertDismissTimer.Start();
        });
    }

    private void StartSmoothOverlayTracking()
    {
    }

    private void TransitionToState(IslandState targetState)
    {
        int gen = ++_transitionGeneration;
        _currentState = targetState;

        double targetWidth;
        double targetHeight;
        CornerRadius targetCornerRadius;
        FrameworkElement incomingGrid;

        switch (targetState)
        {
            case IslandState.Expanded:
                targetWidth = 540;
                targetHeight = 260;
                targetCornerRadius = new CornerRadius(26);
                incomingGrid = ExpandedGrid;
                break;

            case IslandState.Alert:
                bool isVertAlert = IsVerticalMode;
                AlertHorizontalPanel.Visibility = !isVertAlert ? Visibility.Visible : Visibility.Collapsed;
                AlertVerticalPanel.Visibility = isVertAlert ? Visibility.Visible : Visibility.Collapsed;
                if (isVertAlert)
                {
                    targetWidth = 38;
                    targetHeight = 120;
                    targetCornerRadius = new CornerRadius(19);
                }
                else
                {
                    targetWidth = 420;
                    targetHeight = 44;
                    targetCornerRadius = new CornerRadius(22);
                }
                incomingGrid = AlertGrid;
                break;

            case IslandState.Compact:
            default:
                if (IsVerticalMode)
                {
                    targetWidth = 38;
                    targetHeight = CalculateCompactHeight();
                }
                else
                {
                    targetWidth = CalculateCompactWidth();
                    targetHeight = 38;
                }
                targetCornerRadius = new CornerRadius(19);
                incomingGrid = CompactGrid;
                break;
        }

        // ウィンドウ外クリック検知用フックの制御 (展開時のみ有効化)
        if (targetState == IslandState.Expanded)
        {
            InstallMouseHook();
            UpdatePinButtonVisual();
        }
        else
        {
            UninstallMouseHook();
            _isPinned = false;
            UpdatePinButtonVisual();
        }

        // 角丸の更新
        IslandPill.CornerRadius = targetCornerRadius;

        var easeOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var mainDuration = TimeSpan.FromMilliseconds(320);

        var allGrids = new FrameworkElement[] { CompactGrid, ExpandedGrid, AlertGrid };
        var outgoingGrids = allGrids.Where(g => g != incomingGrid).ToList();

        // 1. 退場グリッドのフェードアウト処理
        foreach (var outgoing in outgoingGrids)
        {
            // 以前のアニメーションを確実に解除
            outgoing.BeginAnimation(UIElement.OpacityProperty, null);

            if (outgoing.Visibility == Visibility.Visible && outgoing.Opacity > 0.0)
            {
                double curOpacity = outgoing.Opacity;
                var fadeOut = new DoubleAnimation
                {
                    From = curOpacity,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(90),
                    EasingFunction = easeIn
                };
                fadeOut.Completed += (s, e) =>
                {
                    // 世代チェック: 新しい遷移が既に開始されている場合は過去のコールバックを破棄
                    if (gen != _transitionGeneration) return;

                    outgoing.BeginAnimation(UIElement.OpacityProperty, null);
                    outgoing.Opacity = 0.0;
                    outgoing.Visibility = Visibility.Collapsed;
                };
                outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                outgoing.Opacity = 0.0;
                outgoing.Visibility = Visibility.Collapsed;
            }
        }

        // 2. 登場グリッドのフェードイン処理
        // 過去のアニメーションをクリアし、即座に Visible に設定（非表示化事故を完全に防止）
        incomingGrid.BeginAnimation(UIElement.OpacityProperty, null);
        incomingGrid.Visibility = Visibility.Visible;
        double startOpacity = incomingGrid.Opacity;

        var fadeIn = new DoubleAnimation
        {
            From = startOpacity,
            To = 1.0,
            BeginTime = TimeSpan.FromMilliseconds(80),
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = easeOut
        };
        fadeIn.Completed += (s, e) =>
        {
            if (gen != _transitionGeneration) return;

            incomingGrid.BeginAnimation(UIElement.OpacityProperty, null);
            incomingGrid.Opacity = 1.0;
            incomingGrid.Visibility = Visibility.Visible;
        };
        incomingGrid.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        // 3. カプセル本体のサイズアニメーション (幅 & 高さ)
        double currentW = IslandPill.ActualWidth > 0 ? IslandPill.ActualWidth : targetWidth;
        double currentH = IslandPill.ActualHeight > 0 ? IslandPill.ActualHeight : targetHeight;

        var widthAnim = new DoubleAnimation(currentW, targetWidth, mainDuration) { EasingFunction = easeOut };
        var heightAnim = new DoubleAnimation(currentH, targetHeight, mainDuration) { EasingFunction = easeOut };

        IslandPill.BeginAnimation(WidthProperty, widthAnim);
        IslandPill.BeginAnimation(HeightProperty, heightAnim);

        // 4 & 5. ウィンドウ Left/Top をSlotベースで計算 (画面外クランプ済み)
        var settings = _getSettings();
        double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;

        // アンカーが未設定なら現在位置から初期化
        if (_compactAnchorTop < 0) _compactAnchorTop = Top;
        if (_compactAnchorBottom < 0) _compactAnchorBottom = Top + ActualHeight;
        if (_compactAnchorLeft < 0) _compactAnchorLeft = Left;
        if (_compactAnchorRight < 0) _compactAnchorRight = Left + ActualWidth;
        if (_compactAnchorCenterX < 0) _compactAnchorCenterX = Left + ActualWidth / 2.0;
        if (_compactAnchorCenterY < 0) _compactAnchorCenterY = Top + ActualHeight / 2.0;

        if (targetState == IslandState.Compact)
        {
            IslandPill.BorderThickness = new Thickness(0);
            IslandPill.BorderBrush = Brushes.Transparent;
            // コンパクトに戻る → 元のアンカー位置へ
            var leftBack = new DoubleAnimation(Left, _compactAnchorLeft, mainDuration) { EasingFunction = easeOut };
            var topBack = new DoubleAnimation(Top, _compactAnchorTop, mainDuration) { EasingFunction = easeOut };
            BeginAnimation(LeftProperty, leftBack);
            BeginAnimation(TopProperty, topBack);
        }
        else if (targetState == IslandState.Alert)
        {
            // アラート → GetAlertWindowRect で計算（位置ズレ防止）
            var (alertLeft, alertTop, _, _) = GetAlertWindowRect(settings, scale);
            var leftAnim = new DoubleAnimation(Left, alertLeft, mainDuration) { EasingFunction = easeOut };
            var topAnim = new DoubleAnimation(Top, alertTop, mainDuration) { EasingFunction = easeOut };
            BeginAnimation(LeftProperty, leftAnim);
            BeginAnimation(TopProperty, topAnim);
        }
        else
        {
            // 展開 → GetExpandedWindowRect で計算
            var (expLeft, expTop, _, _) = GetExpandedWindowRect(settings, scale);
            var leftAnim = new DoubleAnimation(Left, expLeft, mainDuration) { EasingFunction = easeOut };
            var topAnim = new DoubleAnimation(Top, expTop, mainDuration) { EasingFunction = easeOut };
            BeginAnimation(LeftProperty, leftAnim);
            BeginAnimation(TopProperty, topAnim);
        }

        StartSmoothOverlayTracking();
    }

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCollapseTimer?.Stop();

        var settings = _getSettings();
        if (settings.AutoExpandOnHover && !_isExpanded && !_isAlertActive)
        {
            _isExpanded = true;
            double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;
            double curH = ActualHeight > 0 ? ActualHeight : ((38 + 32) * scale);
            if (_compactAnchorTop < 0) _compactAnchorTop = Top;
            if (_compactAnchorBottom < 0) _compactAnchorBottom = Top + curH;
            if (_compactAnchorCenterX < 0) _compactAnchorCenterX = Left + ((ActualWidth > 0 ? ActualWidth : 360) / 2.0);
            TransitionToState(IslandState.Expanded);
            ApplyWindowStyle();
        }
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        var settings = _getSettings();
        if (!settings.AutoExpandOnHover || !_isExpanded || _isDragging || _isPinned) return;

        // すぐに縮小せず、わずかなディレイ(250ms)を設けてホバーチャタリングを完全防止
        _hoverCollapseTimer?.Stop();
        _hoverCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hoverCollapseTimer.Tick += (s, args) =>
        {
            _hoverCollapseTimer?.Stop();
            _hoverCollapseTimer = null;

            if (settings.AutoExpandOnHover && _isExpanded && !_isDragging && !_isPinned)
            {
                if (!IslandPill.IsMouseOver)
                {
                    _isExpanded = false;
                    TransitionToState(IslandState.Compact);
                    ApplyWindowStyle();
                }
            }
        };
        _hoverCollapseTimer.Start();
    }

    private void OnIslandPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var settings = _getSettings();

        // 1. ダブルクリックで標準位置へリセット (位置ロック時は無効)
        if (e.ClickCount == 2)
        {
            if (!settings.IsPositionLocked)
            {
                settings.IsCustomPosition = false;
                settings.CustomLeft = -1;
                settings.CustomTop = -1;
                if (settings.Orientation == IslandOrientation.Vertical)
                {
                    settings.PositionSlot = IslandPositionSlot.StartCenter;
                }
                else
                {
                    settings.PositionSlot = IslandPositionSlot.CenterStart;
                }
                _saveSettings(settings);
                _compactAnchorTop = -1;
                _compactAnchorBottom = -1;
                _compactAnchorLeft = -1;
                _compactAnchorRight = -1;
                _compactAnchorCenterX = -1;
                _compactAnchorCenterY = -1;
                ApplyPosition();
            }
            e.Handled = true;
            return;
        }

        // 2. ボタンやコントロール内部クリックの場合はドラッグ開始をスキップ
        if (e.OriginalSource is DependencyObject dep)
        {
            var btn = FindVisualParent<Button>(dep);
            if (btn != null)
            {
                _isPotentialDrag = false;
                _isDragging = false;
                return;
            }
        }

        _isDragging = false;
        _isPotentialDrag = true;
        _dragStartPos = e.GetPosition(this);
    }

    private void OnIslandPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPotentialDrag || e.LeftButton != MouseButtonState.Pressed) return;

        var settings = _getSettings();
        // 位置ロックが有効な場合はドラッグを完全に禁止
        if (settings.IsPositionLocked) return;

        Point currentPos = e.GetPosition(this);
        Vector diff = currentPos - _dragStartPos;

        // Windows 標準のドラッグ閾値（約4px）以上移動した場合のみドラッグを開始
        if (Math.Abs(diff.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            _isDragging = true;
            _isPotentialDrag = false;

            try
            {
                // ドラッグ開始前にアニメーションを完全にクリア
                BeginAnimation(LeftProperty, null);
                BeginAnimation(TopProperty, null);

                // ドラッグ時はカプセルをコンパクト化して正確な設置位置決めを可能にする
                if (_isExpanded)
                {
                    _isExpanded = false;
                    TransitionToState(IslandState.Compact);
                    UpdateLayout();
                }

                DragMove();

                // 実際のドラッグ完了後にのみカスタム座標を保存・アンカーを更新
                settings.IsCustomPosition = true;
                settings.CustomLeft = Left;
                settings.CustomTop = Top;
                _saveSettings(settings);

                double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;
                double curPillW = IsVerticalMode ? 38 : (IslandPill.ActualWidth > 0 ? IslandPill.ActualWidth : CalculateCompactWidth());
                double curPillH = IsVerticalMode ? (IslandPill.ActualHeight > 0 ? IslandPill.ActualHeight : CalculateCompactHeight()) : 38;
                double totalW = (curPillW + 32) * scale;
                double curH = (curPillH + 32) * scale;

                _compactAnchorLeft = Left;
                _compactAnchorRight = Left + totalW;
                _compactAnchorTop = Top;
                _compactAnchorBottom = Top + curH;
                _compactAnchorCenterX = Left + (totalW / 2.0);
                _compactAnchorCenterY = Top + (curH / 2.0);
            }
            catch { }
        }
    }

    private void OnIslandPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            _isPotentialDrag = false;
            e.Handled = true;
            return;
        }

        if (_isPotentialDrag)
        {
            _isPotentialDrag = false;

            // ボタンクリック等でなければ、Compact モード時のクリックで展開
            if (e.OriginalSource is DependencyObject dep)
            {
                var btn = FindVisualParent<Button>(dep);
                if (btn != null) return;
            }

            if (_currentState == IslandState.Compact)
            {
                _hoverCollapseTimer?.Stop();
                _isExpanded = true;
                double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;
                double curH = ActualHeight > 0 ? ActualHeight : ((38 + 32) * scale);
                if (_compactAnchorTop < 0) _compactAnchorTop = Top;
                if (_compactAnchorBottom < 0) _compactAnchorBottom = Top + curH;
                if (_compactAnchorCenterX < 0) _compactAnchorCenterX = Left + ((ActualWidth > 0 ? ActualWidth : 360) / 2.0);
                TransitionToState(IslandState.Expanded);
                ApplyWindowStyle();
                e.Handled = true;
            }
            else if (_currentState == IslandState.Alert)
            {
                _alertDismissTimer?.Stop();
                _isAlertActive = false;
                _pulseStoryboard?.Stop(this);
                GlowEffect.Color = Colors.Black;
                _hoverCollapseTimer?.Stop();
                _isExpanded = true;
                double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;
                double curH = ActualHeight > 0 ? ActualHeight : ((38 + 32) * scale);
                if (_compactAnchorTop < 0) _compactAnchorTop = Top;
                if (_compactAnchorBottom < 0) _compactAnchorBottom = Top + curH;
                if (_compactAnchorCenterX < 0) _compactAnchorCenterX = Left + ((ActualWidth > 0 ? ActualWidth : 360) / 2.0);
                TransitionToState(IslandState.Expanded);
                ApplyWindowStyle();
                e.Handled = true;
            }
        }
    }

    private void OnIslandPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging) return;

        if (e.OriginalSource is DependencyObject dep)
        {
            var btn = FindVisualParent<System.Windows.Controls.Button>(dep);
            if (btn != null) return;
        }

        e.Handled = true;
        ShowIslandContextMenu();
    }

    private void ShowIslandContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = PlacementMode.MousePoint
        };
        _activeContextMenu = menu;
        menu.Closed += (s, e) =>
        {
            if (_activeContextMenu == menu) _activeContextMenu = null;
        };
        EnableDismissOnOutsideClick(menu);

        var loc = LocalizationService.Instance;
        var settings = _getSettings();

        // 1. ダッシュボードを開く
        var dashboardItem = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_Menu_OpenDashboard"],
            FontWeight = FontWeights.SemiBold,
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.Apps24, FontSize = 16 }
        };
        dashboardItem.Click += (s, e) => NavigateTo("Dashboard");
        menu.Items.Add(dashboardItem);

        // 2. OmniGlance 設定
        var settingsItem = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_Menu_Settings"],
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.Settings24, FontSize = 16 }
        };
        settingsItem.Click += (s, e) => NavigateTo("OmniGlance");
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // 3. 詳細展開 / 折りたたみトグル
        var toggleExpandItem = new System.Windows.Controls.MenuItem
        {
            Header = _isExpanded ? loc["OmniGlance_Menu_Collapse"] : loc["OmniGlance_Menu_Expand"],
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = _isExpanded ? SymbolRegular.ChevronUp24 : SymbolRegular.ChevronDown24,
                FontSize = 16
            }
        };
        toggleExpandItem.Click += (s, e) =>
        {
            _hoverCollapseTimer?.Stop();
            _isExpanded = !_isExpanded;
            TransitionToState(_isExpanded ? IslandState.Expanded : IslandState.Compact);
            ApplyWindowStyle();
        };
        menu.Items.Add(toggleExpandItem);

        // 4. 位置を固定 (ロック)
        var lockItem = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_Menu_LockPosition"],
            IsCheckable = true,
            IsChecked = settings.IsPositionLocked,
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.Pin24, FontSize = 16 }
        };
        lockItem.Click += (s, e) =>
        {
            settings.IsPositionLocked = lockItem.IsChecked;
            _saveSettings(settings);
        };
        menu.Items.Add(lockItem);

        // 5. 表示モード サブメニュー
        var orientSubMenu = new System.Windows.Controls.MenuItem
        {
            Header = "表示モード",
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.Orientation24, FontSize = 16 }
        };

        var horizItem = new System.Windows.Controls.MenuItem
        {
            Header = "横表示モード",
            IsCheckable = true,
            IsChecked = settings.Orientation == IslandOrientation.Horizontal
        };
        horizItem.Click += (s, e) =>
        {
            settings.Orientation = IslandOrientation.Horizontal;
            settings.PositionSlot = IslandPositionSlot.CenterStart;
            settings.IsCustomPosition = false;
            settings.CustomLeft = -1;
            settings.CustomTop = -1;
            settings.HasMigratedToSlots = true;
            _saveSettings(settings);
            ApplySettings();
        };
        orientSubMenu.Items.Add(horizItem);

        var vertItem = new System.Windows.Controls.MenuItem
        {
            Header = "縦表示モード",
            IsCheckable = true,
            IsChecked = settings.Orientation == IslandOrientation.Vertical
        };
        vertItem.Click += (s, e) =>
        {
            settings.Orientation = IslandOrientation.Vertical;
            settings.PositionSlot = IslandPositionSlot.StartCenter;
            settings.IsCustomPosition = false;
            settings.CustomLeft = -1;
            settings.CustomTop = -1;
            settings.HasMigratedToSlots = true;
            _saveSettings(settings);
            ApplySettings();
        };
        orientSubMenu.Items.Add(vertItem);
        menu.Items.Add(orientSubMenu);

        // 6. 配置サブメニュー
        var posSubMenu = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_Menu_Position"],
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.PositionForward24, FontSize = 16 }
        };

        (IslandPositionSlot slot, string label)[] slots;
        if (settings.Orientation == IslandOrientation.Vertical)
        {
            slots = new (IslandPositionSlot, string)[]
            {
                (IslandPositionSlot.StartStart, "左上"),
                (IslandPositionSlot.StartCenter, "左"),
                (IslandPositionSlot.StartEnd, "左下"),
                (IslandPositionSlot.EndStart, "右上"),
                (IslandPositionSlot.EndCenter, "右"),
                (IslandPositionSlot.EndEnd, "右下")
            };
        }
        else
        {
            slots = new (IslandPositionSlot, string)[]
            {
                (IslandPositionSlot.StartStart, "左上"),
                (IslandPositionSlot.CenterStart, "上"),
                (IslandPositionSlot.EndStart, "右上"),
                (IslandPositionSlot.CenterEnd, "下"),
                (IslandPositionSlot.StartEnd, "左下"),
                (IslandPositionSlot.EndEnd, "右下")
            };
        }

        foreach (var (slot, label) in slots)
        {
            var pItem = new System.Windows.Controls.MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = !settings.IsCustomPosition && (settings.PositionSlot == slot)
            };
            pItem.Click += (s, e) =>
            {
                settings.IsCustomPosition = false;
                settings.CustomLeft = -1;
                settings.CustomTop = -1;
                settings.PositionSlot = slot;
                _saveSettings(settings);
                ApplySettings();
            };
            posSubMenu.Items.Add(pItem);
        }
        menu.Items.Add(posSubMenu);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // 6. カレンダー同期
        var syncItem = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_Menu_SyncCalendar"],
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.ArrowSync24, FontSize = 16 }
        };
        syncItem.Click += async (s, e) =>
        {
            if (_calendarService != null)
            {
                await _calendarService.SyncAsync();
            }
        };
        menu.Items.Add(syncItem);

        try
        {
            this.Activate();
        }
        catch { }

        menu.PlacementTarget = IslandPill;
        menu.IsOpen = true;
    }

    private void NavigateTo(string target)
    {
        if (_navigateAction != null)
        {
            _navigateAction(target);
            return;
        }

        try
        {
            if (Application.Current.MainWindow is Window mainWin)
            {
                mainWin.Show();
                if (mainWin.WindowState == WindowState.Minimized)
                {
                    mainWin.WindowState = WindowState.Normal;
                }
                mainWin.Activate();

                var navMethod = mainWin.GetType().GetMethod("NavigateToModule");
                navMethod?.Invoke(mainWin, new object[] { target });
            }
        }
        catch { }
    }

    private static void EnableDismissOnOutsideClick(System.Windows.Controls.ContextMenu menu)
    {
        menu.Opened += (s, e) =>
        {
            try
            {
                Mouse.Capture(menu, CaptureMode.SubTree);
            }
            catch { }
        };

        Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(menu, (s, e) =>
        {
            menu.IsOpen = false;
            try
            {
                if (Mouse.Captured == menu)
                {
                    Mouse.Capture(null);
                }
            }
            catch { }
        });

        menu.Closed += (s, e) =>
        {
            try
            {
                if (Mouse.Captured == menu)
                {
                    Mouse.Capture(null);
                }
            }
            catch { }
        };
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;

        _expandedTime = DateTime.UtcNow;
        _mouseProc = MouseHookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        IntPtr hModule = NativeMethods.GetModuleHandle(curModule?.ModuleName);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hModule, 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isExpanded && !_isDragging && !_isPinned)
        {
            // 展開直後 (200ms 以内) のクリックチャタリングを防止
            if ((DateTime.UtcNow - _expandedTime).TotalMilliseconds > 200)
            {
                int msg = wParam.ToInt32();
                if (msg == NativeMethods.WM_LBUTTONDOWN ||
                    msg == NativeMethods.WM_RBUTTONDOWN ||
                    msg == NativeMethods.WM_MBUTTONDOWN)
                {
                    var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    if (!IsPointInsideAnyIslandElement(hookStruct.pt.X, hookStruct.pt.Y))
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (_isExpanded && !_isDragging)
                            {
                                _hoverCollapseTimer?.Stop();
                                _hoverCollapseTimer = null;
                                _isExpanded = false;
                                TransitionToState(IslandState.Compact);
                                ApplyWindowStyle();
                            }
                        });
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideAnyIslandElement(int screenX, int screenY)
    {
        // 1. コンテキストメニューが開いている場合はクリック除外
        if (_activeContextMenu?.IsOpen == true)
        {
            return true;
        }

        // 2. 島本体カプセル (IslandPill) の内部判定
        try
        {
            if (IslandPill.IsVisible)
            {
                Point pt = IslandPill.PointFromScreen(new Point(screenX, screenY));
                if (pt.X >= 0 && pt.X <= IslandPill.ActualWidth && pt.Y >= 0 && pt.Y <= IslandPill.ActualHeight)
                {
                    return true;
                }
            }
        }
        catch { }

        // 3. クリックされたウィンドウハンドルが自前ウィンドウまたはその子要素か判定
        try
        {
            var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
            var clickedHwnd = NativeMethods.WindowFromPoint(pt);
            if (clickedHwnd != IntPtr.Zero)
            {
                var islandHwnd = new WindowInteropHelper(this).Handle;
                if (islandHwnd != IntPtr.Zero)
                {
                    var root = NativeMethods.GetAncestor(clickedHwnd, NativeMethods.GA_ROOT);
                    if (root == islandHwnd || clickedHwnd == islandHwnd)
                    {
                        return true;
                    }
                }

                // モーダル編集ウィンドウやその他アプリ内ウィンドウの判定
                foreach (Window win in Application.Current.Windows)
                {
                    if (win.IsVisible)
                    {
                        var winHwnd = new WindowInteropHelper(win).Handle;
                        if (winHwnd != IntPtr.Zero)
                        {
                            var root = NativeMethods.GetAncestor(clickedHwnd, NativeMethods.GA_ROOT);
                            if (root == winHwnd || clickedHwnd == winHwnd)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private void OnCalendarToggleClicked(object sender, RoutedEventArgs e)
    {
        if (CalendarViewGrid.Visibility == Visibility.Visible)
        {
            CalendarViewGrid.Visibility = Visibility.Collapsed;
            StatusViewGrid.Visibility = Visibility.Visible;
            CalendarToggleBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent;
        }
        else
        {
            CalendarViewGrid.Visibility = Visibility.Visible;
            StatusViewGrid.Visibility = Visibility.Collapsed;
            CalendarToggleBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            PopulateCalendar(_currentCalendarMonth);
            UpdateSelectedDateEvents(_selectedCalendarDate);
        }
    }

    private void OnCalendarServiceUpdated()
    {
        Dispatcher.InvokeAsync(() =>
        {
            PopulateCalendar(_currentCalendarMonth);
            UpdateSelectedDateEvents(_selectedCalendarDate);
            UpdateCalendarIndicator();
        });
    }

    private void PopulateCalendar(DateTime targetMonth)
    {
        CalendarMonthTitleText.Text = targetMonth.ToString("yyyy年 M月");

        _calendarDayCells.Clear();

        var firstDayOfMonth = new DateTime(targetMonth.Year, targetMonth.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month);

        int startDayOfWeek = (int)firstDayOfMonth.DayOfWeek;

        // 前月埋め
        var prevMonth = targetMonth.AddMonths(-1);
        int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
        for (int i = startDayOfWeek - 1; i >= 0; i--)
        {
            var d = new DateTime(prevMonth.Year, prevMonth.Month, daysInPrevMonth - i);
            _calendarDayCells.Add(CreateDayCell(d, false));
        }

        // 当月
        for (int day = 1; day <= daysInMonth; day++)
        {
            var d = new DateTime(targetMonth.Year, targetMonth.Month, day);
            _calendarDayCells.Add(CreateDayCell(d, true));
        }

        // 次月埋め (合計42セル)
        int totalCells = 42;
        int nextMonthDays = totalCells - _calendarDayCells.Count;
        var nextMonth = targetMonth.AddMonths(1);
        for (int day = 1; day <= nextMonthDays; day++)
        {
            var d = new DateTime(nextMonth.Year, nextMonth.Month, day);
            _calendarDayCells.Add(CreateDayCell(d, false));
        }
    }

    private CalendarDayCell CreateDayCell(DateTime d, bool isCurrentMonth)
    {
        var cell = new CalendarDayCell
        {
            Date = d,
            IsCurrentMonth = isCurrentMonth,
            IsToday = d.Date == DateTime.Today,
            IsSelected = d.Date == _selectedCalendarDate.Date,
            HasEvents = _calendarService?.HasEventsOnDate(d) ?? false
        };

        if (_calendarService != null)
        {
            var colors = _calendarService.GetDistinctColorsForDate(d);
            foreach (var brush in colors)
            {
                cell.EventDotBrushes.Add(brush);
            }
        }

        return cell;
    }

    private void UpdateSelectedDateEvents(DateTime date)
    {
        SelectedDateHeaderText.Text = date.ToString("M月d日 (ddd) の予定");

        var evs = _calendarService?.GetEventsForDate(date) ?? new List<CalendarEvent>();
        DateEventsItemsControl.ItemsSource = evs;
        NoEventsPlaceholder.Visibility = evs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_calendarService != null)
        {
            CalendarSyncStatusText.Text = $"同期: {_calendarService.LastSyncStatus}";
        }

        DateEventsScrollViewer.Visibility = Visibility.Visible;
    }

    private void OnCalendarPrevMonthClicked(object sender, RoutedEventArgs e)
    {
        _currentCalendarMonth = _currentCalendarMonth.AddMonths(-1);
        PopulateCalendar(_currentCalendarMonth);
    }

    private void OnCalendarNextMonthClicked(object sender, RoutedEventArgs e)
    {
        _currentCalendarMonth = _currentCalendarMonth.AddMonths(1);
        PopulateCalendar(_currentCalendarMonth);
    }

    private void OnCalendarTodayClicked(object sender, RoutedEventArgs e)
    {
        _currentCalendarMonth = DateTime.Today;
        _selectedCalendarDate = DateTime.Today;
        PopulateCalendar(_currentCalendarMonth);
        UpdateSelectedDateEvents(_selectedCalendarDate);
    }

    private void OnCalendarDayCellClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CalendarDayCell cell)
        {
            _selectedCalendarDate = cell.Date;

            if (cell.Date.Month != _currentCalendarMonth.Month || cell.Date.Year != _currentCalendarMonth.Year)
            {
                _currentCalendarMonth = new DateTime(cell.Date.Year, cell.Date.Month, 1);
                PopulateCalendar(_currentCalendarMonth);
            }
            else
            {
                foreach (var c in _calendarDayCells)
                {
                    c.IsSelected = (c.Date.Date == _selectedCalendarDate.Date);
                }
            }

            UpdateSelectedDateEvents(_selectedCalendarDate);
        }
    }

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        UpdatePinButtonVisual();
    }

    private void UpdatePinButtonVisual()
    {
        if (PinBtn == null || PinBtnIcon == null) return;

        if (_isPinned)
        {
            PinBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            PinBtn.ToolTip = "ピン留め解除 (外側クリックで自動格納)";
            PinBtnIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
        }
        else
        {
            PinBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent;
            PinBtn.ToolTip = "ピン留め (常に展開表示を維持)";
            PinBtnIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
        }
    }

    private void OnCollapseClicked(object sender, RoutedEventArgs e)
    {
        _hoverCollapseTimer?.Stop();
        _isExpanded = false;
        _isPinned = false;
        UpdatePinButtonVisual();
        _isAlertActive = false;
        _pulseStoryboard?.Stop(this);
        GlowEffect.Color = Colors.Black;
        TransitionToState(IslandState.Compact);
        ApplyWindowStyle();
    }

    private void UpdateCalendarIndicator()
    {
        if (_calendarService == null)
        {
            CalendarIndicatorCompact.Visibility = Visibility.Collapsed;
            CalendarIndicatorVertical.Visibility = Visibility.Collapsed;
            return;
        }

        var todayEvents = _calendarService.GetEventsForDate(DateTime.Today);
        if (todayEvents.Count > 0)
        {
            var firstEvent = todayEvents.FirstOrDefault(e => e.EndTime > DateTime.Now) ?? todayEvents[0];
            var brush = firstEvent.ColorBrush;

            CalendarIndicatorCompact.Background = brush;
            CalendarIndicatorCompact.Visibility = Visibility.Visible;
            CalendarIndicatorCompact.ToolTip = $"今日の予定 ({todayEvents.Count}件)\n・{firstEvent.Title} ({firstEvent.TimeText})";

            CalendarIndicatorVertical.Background = brush;
            CalendarIndicatorVertical.Visibility = Visibility.Visible;
            CalendarIndicatorVertical.ToolTip = $"今日の予定 ({todayEvents.Count}件)\n・{firstEvent.Title} ({firstEvent.TimeText})";
        }
        else
        {
            CalendarIndicatorCompact.Visibility = Visibility.Collapsed;
            CalendarIndicatorVertical.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnAddEventClicked(object sender, RoutedEventArgs e)
    {
        var settings = _getSettings();
        bool isGoogleAvailable = settings.GoogleSyncEnabled && _calendarService?.GoogleAuth.IsSignedIn == true;
        bool isICloudAvailable = settings.ICloudSyncEnabled && !string.IsNullOrWhiteSpace(settings.ICloudAppleId);

        var editor = new CalendarEventEditorWindow(null, _selectedCalendarDate, settings, isGoogleAvailable, isICloudAvailable)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true && editor.ResultEvent != null)
        {
            if (_calendarService != null)
            {
                await _calendarService.SaveEventAsync(editor.ResultEvent);
            }
            PopulateCalendar(_currentCalendarMonth);
            UpdateSelectedDateEvents(_selectedCalendarDate);
            UpdateCalendarIndicator();
        }
    }

    private async void OnEditEventClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CalendarEvent ev)
        {
            var settings = _getSettings();
            bool isGoogleAvailable = settings.GoogleSyncEnabled && _calendarService?.GoogleAuth.IsSignedIn == true;
            bool isICloudAvailable = settings.ICloudSyncEnabled && !string.IsNullOrWhiteSpace(settings.ICloudAppleId);

            var editor = new CalendarEventEditorWindow(ev, ev.StartTime, settings, isGoogleAvailable, isICloudAvailable)
            {
                Owner = this
            };

            if (editor.ShowDialog() == true)
            {
                if (editor.IsDeleted)
                {
                    if (_calendarService != null)
                    {
                        await _calendarService.DeleteEventAsync(ev);
                    }
                }
                else if (editor.ResultEvent != null)
                {
                    if (_calendarService != null)
                    {
                        await _calendarService.SaveEventAsync(editor.ResultEvent);
                    }
                }
                PopulateCalendar(_currentCalendarMonth);
                UpdateSelectedDateEvents(_selectedCalendarDate);
                UpdateCalendarIndicator();
            }
        }
    }

    private void OnOpenEventWebClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CalendarEvent ev)
        {
            if (ev.ProviderType == CalendarProviderType.GoogleCalendar)
            {
                GoogleCalendarService.OpenEventInGoogleCalendar(ev);
            }
            else if (ev.ProviderType == CalendarProviderType.AppleICloud)
            {
                GoogleCalendarService.OpenICloudCalendar();
            }
            else if (!string.IsNullOrWhiteSpace(ev.Url))
            {
                GoogleCalendarService.OpenUrl(ev.Url);
            }
            else
            {
                GoogleCalendarService.OpenDateInGoogleCalendar(ev.StartTime);
            }
        }
    }

    private async void OnDeleteCustomEventClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CalendarEvent ev)
        {
            var result = MessageBox.Show(
                $"予定「{ev.Title}」を削除してもよろしいですか？",
                "予定の削除確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            if (_calendarService != null)
            {
                await _calendarService.DeleteEventAsync(ev);
            }

            var settings = _getSettings();
            settings.CustomEvents.RemoveAll(x => x.Id == ev.Id);
            _saveSettings(settings);

            PopulateCalendar(_currentCalendarMonth);
            UpdateSelectedDateEvents(_selectedCalendarDate);
            UpdateCalendarIndicator();
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
