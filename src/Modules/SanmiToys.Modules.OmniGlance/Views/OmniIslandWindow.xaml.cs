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
using SanmiToys.Modules.OmniGlance.Core;
using SanmiToys.Modules.OmniGlance.Helpers;
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

    private DateTime _lastCpuAlertTime = DateTime.MinValue;
    private DateTime _lastGpuAlertTime = DateTime.MinValue;
    private DateTime _lastRamAlertTime = DateTime.MinValue;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromSeconds(90);
    private readonly System.Windows.Data.CollectionViewSource _islandBatteryViewSource = new();

    /// <summary>画面端マージン: アラート時の枠線(1.5px)やグローが画面端・タスクバーに隠れないよう十分な隙間(6px)を確保</summary>
    private const double ScreenEdgeVGap = 6.0;
    private const double ScreenEdgeHGap = 6.0;

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

        _islandBatteryViewSource.Source = _batteryService.Devices;
        _islandBatteryViewSource.Filter += (s, e) =>
        {
            var settings = _getSettings();
            if (e.Item is DeviceBatteryInfo dev)
            {
                bool isDis = settings.DisabledDeviceIds.Contains(dev.Id) || settings.DisabledDeviceIds.Contains(dev.Name);
                e.Accepted = !isDis;
            }
            else
            {
                e.Accepted = true;
            }
        };

        _pulseStoryboard = TryFindResource("PulseGlowStoryboard") as Storyboard;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BatteryCompactItemsControl.ItemsSource = _islandBatteryViewSource.View;
        BatteryVerticalItemsControl.ItemsSource = _islandBatteryViewSource.View;
        ExpandedDeviceItemsControl.ItemsSource = _islandBatteryViewSource.View;

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

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ApplyPosition();
        });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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

            // 透過ウィンドウ互換性ヘルパーを適用（TranslucentTB/DWMアクリル注入による外周余白ブラーを根本防止）
            WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

            // Windows 11 DWM システムバックドロップ・境界線枠線を確実に無効化し、四角い背景化を根絶
            try
            {
                const int DWMWA_BORDER_COLOR = 34;
                int borderColorNone = unchecked((int)0xFFFFFFFE);
                NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColorNone, sizeof(int));

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

    /// <summary>
    /// 最前面表示状態を適用する。
    /// WPFのTopmostプロパティおよびWin32のSetWindowPos(HWND_TOPMOST/HWND_NOTOPMOST)で確実に反映する。
    /// </summary>
    public void ApplyAlwaysOnTop(bool alwaysOnTop)
    {
        this.Topmost = alwaysOnTop;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                alwaysOnTop ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void ResetPillBackground()
    {
        var settings = _getSettings();
        byte alpha = (byte)Math.Clamp((int)(settings.Opacity * 255), 1, 255);
        IslandPill.Background = new SolidColorBrush(Color.FromArgb(alpha, 11, 12, 14));
        IslandPill.BorderThickness = new Thickness(0);
        IslandPill.BorderBrush = Brushes.Transparent;
    }

    public void ApplySettings()
    {
        var settings = _getSettings();

        // 後方互換: 旧PositionModeが設定されていて新Orientationがデフォルトの場合はマイグレーション
        MigrateLegacyPositionIfNeeded(settings);

        // 1. 不透明度 (背景のみに適用し、テキストやアイコン等のコンテンツは不透明度100%を維持)
        IslandPill.Opacity = 1.0;
        if (!_isAlertActive)
        {
            ResetPillBackground();
        }

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

        // カレンダーインジケータの表示・マージン更新
        UpdateCalendarIndicator();

        // バッテリーデバイスの表示フィルタ更新 (設定で無効化されたデバイスをスマートに除外)
        _islandBatteryViewSource.View?.Refresh();

        // 4. 位置ロック
        IslandPill.Cursor = settings.IsPositionLocked ? Cursors.Arrow : Cursors.Hand;

        ApplyPosition();
        ApplyWindowStyle();
        ApplyAlwaysOnTop(settings.AlwaysOnTop);
        ApplyColorMode();

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
            ApplyPosition();
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

        ClockPerfSeparator.Visibility = (hasClock && hasPerf) ? Visibility.Visible : Visibility.Collapsed;
        PerfBatterySeparator.Visibility = (hasPerf && hasBattery) ? Visibility.Visible : Visibility.Collapsed;

        ClockPerfVerticalSeparator.Visibility = (hasClock && hasPerf) ? Visibility.Visible : Visibility.Collapsed;
        PerfBatteryVerticalSeparator.Visibility = (hasPerf && hasBattery) ? Visibility.Visible : Visibility.Collapsed;
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

    private void ApplyColorMode()
    {
        var settings = _getSettings();
        bool isMonochrome = settings.ColorMode == IslandColorMode.Monochrome;
        bool showBadgeBg = settings.ShowBadgeBackground;

        // 1. デバイスバッテリー情報のモノトーンフラグ & バッジ背景フラグ更新 & 全再描画
        DeviceBatteryInfo.IsMonochromeMode = isMonochrome;
        DeviceBatteryInfo.ShowBadgeBackground = showBadgeBg;
        if (_batteryService?.Devices != null)
        {
            foreach (var dev in _batteryService.Devices)
            {
                dev.RefreshDisplayBrushes();
            }
        }

        // 2. パフォーマンス部分の色定義
        var whiteBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
        whiteBrush.Freeze();
        var whiteBgBrush = showBadgeBg ? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        if (whiteBgBrush.CanFreeze) whiteBgBrush.Freeze();
        var progMonoBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        progMonoBrush.Freeze();

        // カラーモード時の色
        var cpuBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00));
        cpuBrush.Freeze();
        var cpuBgBrush = showBadgeBg ? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xB9, 0x00)) : Brushes.Transparent;
        if (cpuBgBrush.CanFreeze) cpuBgBrush.Freeze();

        var gpuBrush = new SolidColorBrush(Color.FromRgb(0x70, 0xB0, 0xFF));
        gpuBrush.Freeze();
        var gpuBgBrush = showBadgeBg ? new SolidColorBrush(Color.FromArgb(0x26, 0x70, 0xB0, 0xFF)) : Brushes.Transparent;
        if (gpuBgBrush.CanFreeze) gpuBgBrush.Freeze();

        var ramBrush = new SolidColorBrush(Color.FromRgb(0x26, 0xE0, 0x7F));
        ramBrush.Freeze();
        var ramBgBrush = showBadgeBg ? new SolidColorBrush(Color.FromArgb(0x26, 0x26, 0xE0, 0x7F)) : Brushes.Transparent;
        if (ramBgBrush.CanFreeze) ramBgBrush.Freeze();

        var powerBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x82, 0x20));
        powerBrush.Freeze();
        var powerBgBrush = showBadgeBg ? new SolidColorBrush(Color.FromArgb(0x26, 0xF5, 0x82, 0x20)) : Brushes.Transparent;
        if (powerBgBrush.CanFreeze) powerBgBrush.Freeze();

        // 適用
        if (isMonochrome)
        {
            CpuCompactIcon.Foreground = whiteBrush;
            CpuCompactBorder.Background = whiteBgBrush;
            GpuCompactIcon.Foreground = whiteBrush;
            GpuCompactBorder.Background = whiteBgBrush;
            RamCompactIcon.Foreground = whiteBrush;
            RamCompactBorder.Background = whiteBgBrush;
            PowerCompactIcon.Foreground = whiteBrush;
            PowerCompactBorder.Background = whiteBgBrush;

            CpuVerticalIcon.Foreground = whiteBrush;
            CpuVerticalBorder.Background = whiteBgBrush;
            GpuVerticalIcon.Foreground = whiteBrush;
            GpuVerticalBorder.Background = whiteBgBrush;
            RamVerticalIcon.Foreground = whiteBrush;
            RamVerticalBorder.Background = whiteBgBrush;
            PowerVerticalIcon.Foreground = whiteBrush;
            PowerVerticalBorder.Background = whiteBgBrush;

            CpuExpandedIcon.Foreground = whiteBrush;
            CpuProgressBar.Foreground = progMonoBrush;
            CpuExpandedBorder.Background = whiteBgBrush;
            CpuTempBorder.Background = whiteBgBrush;
            CpuTempIcon.Foreground = whiteBrush;

            GpuExpandedIcon.Foreground = whiteBrush;
            GpuProgressBar.Foreground = progMonoBrush;
            GpuExpandedBorder.Background = whiteBgBrush;
            GpuTempBorder.Background = whiteBgBrush;
            GpuTempIcon.Foreground = whiteBrush;

            RamExpandedIcon.Foreground = whiteBrush;
            RamProgressBar.Foreground = progMonoBrush;
            RamExpandedBorder.Background = whiteBgBrush;

            PowerExpandedIcon.Foreground = whiteBrush;
            PowerExpandedBorder.Background = whiteBgBrush;
        }
        else
        {
            CpuCompactIcon.Foreground = cpuBrush;
            CpuCompactBorder.Background = cpuBgBrush;
            GpuCompactIcon.Foreground = gpuBrush;
            GpuCompactBorder.Background = gpuBgBrush;
            RamCompactIcon.Foreground = ramBrush;
            RamCompactBorder.Background = ramBgBrush;
            PowerCompactIcon.Foreground = powerBrush;
            PowerCompactBorder.Background = powerBgBrush;

            CpuVerticalIcon.Foreground = cpuBrush;
            CpuVerticalBorder.Background = cpuBgBrush;
            GpuVerticalIcon.Foreground = gpuBrush;
            GpuVerticalBorder.Background = gpuBgBrush;
            RamVerticalIcon.Foreground = ramBrush;
            RamVerticalBorder.Background = ramBgBrush;
            PowerVerticalIcon.Foreground = powerBrush;
            PowerVerticalBorder.Background = powerBgBrush;

            CpuExpandedIcon.Foreground = cpuBrush;
            CpuProgressBar.Foreground = cpuBrush;
            CpuExpandedBorder.Background = cpuBgBrush;
            CpuTempBorder.Background = cpuBgBrush;
            CpuTempIcon.Foreground = cpuBrush;

            GpuExpandedIcon.Foreground = gpuBrush;
            GpuProgressBar.Foreground = gpuBrush;
            GpuExpandedBorder.Background = gpuBgBrush;
            GpuTempBorder.Background = gpuBgBrush;
            GpuTempIcon.Foreground = gpuBrush;

            RamExpandedIcon.Foreground = ramBrush;
            RamProgressBar.Foreground = ramBrush;
            RamExpandedBorder.Background = ramBgBrush;

            PowerExpandedIcon.Foreground = powerBrush;
            PowerExpandedBorder.Background = powerBgBrush;
        }
    }

    private ScreenDpiBounds GetCurrentScreenBounds(OmniGlanceSettings settings)
    {
        return OmniScreenHelper.GetTargetScreenBounds(settings.TargetMonitorDeviceName, settings.AllowTaskbarPlacement);
    }

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

        var sb = GetCurrentScreenBounds(settings);
        double screenW = sb.ScreenWidth;
        double screenH = sb.ScreenHeight;
        double screenLeft = sb.ScreenLeft;
        double screenTop = sb.ScreenTop;
        double workTop = sb.WorkTop;
        double workBottom = sb.WorkBottom;
        double workLeft = sb.WorkLeft;
        double workRight = sb.WorkRight;

        const double vGap = ScreenEdgeVGap;
        const double hGap = ScreenEdgeHGap;

        double leftEdge = workLeft + hGap - (16 * scale);
        double rightEdge = workRight - windowW + (16 * scale) - hGap;
        double topEdge = workTop + vGap - (16 * scale);
        double bottomEdge = workBottom - vGap - (windowH - 16 * scale);
        double centerX = screenLeft + (screenW - windowW) / 2.0;
        double centerY = screenTop + (screenH - windowH) / 2.0;

        double targetLeft, targetTop;

        if (settings.IsCustomPosition && settings.CustomLeft >= 0 && settings.CustomTop >= 0)
        {
            targetLeft = Math.Clamp(settings.CustomLeft, leftEdge, Math.Max(leftEdge, rightEdge));
            targetTop = Math.Clamp(settings.CustomTop, topEdge, Math.Max(topEdge, bottomEdge));
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
        OmniGlanceSettings settings, double scale, double? customPillW = null, double? customPillH = null)
    {
        bool isVertical = settings.Orientation == IslandOrientation.Vertical;

        double pillW;
        double pillH;
        if (customPillW.HasValue && customPillH.HasValue)
        {
            pillW = customPillW.Value;
            pillH = customPillH.Value;
        }
        else
        {
            (pillW, pillH) = CalculateExpandedSize();
        }

        var sb = GetCurrentScreenBounds(settings);
        double screenW = sb.ScreenWidth;
        double screenH = sb.ScreenHeight;
        double screenLeft = sb.ScreenLeft;
        double screenTop = sb.ScreenTop;
        double workTop = sb.WorkTop;
        double workBottom = sb.WorkBottom;
        double workLeft = sb.WorkLeft;
        double workRight = sb.WorkRight;
        double workW = workRight - workLeft;
        double workH = workBottom - workTop;
        const double vGap = ScreenEdgeVGap;
        const double hGap = ScreenEdgeHGap;

        // 作業領域（タスクバー・画面端）内に完全に収まるよう、ピル最大サイズを安全クランプ
        double maxPillW = Math.Max(240, (workW - (hGap * 2)) / scale - 32);
        double maxPillH = Math.Max(140, (workH - (vGap * 2)) / scale - 32);
        pillW = Math.Min(pillW, maxPillW);
        pillH = Math.Min(pillH, maxPillH);

        double winW = (pillW + 32) * scale;
        double winH = (pillH + 32) * scale;

        // コンパクト時とミリ単位で完全一致する画面端境界値
        double leftEdge = workLeft + hGap - (16 * scale);
        double rightEdge = workRight - winW + (16 * scale) - hGap;
        double topEdge = workTop + vGap - (16 * scale);
        double bottomEdge = workBottom - winH + (16 * scale) - vGap;
        double centerX = workLeft + (workW - winW) / 2.0;
        double centerY = workTop + (workH - winH) / 2.0;

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
                bool isRight = ancLeft > workLeft + (workW / 2.0);
                targetLeft = isRight ? rightEdge : leftEdge;

                double relCenter = (ancCenterY - workTop) / workH;
                if (relCenter < 0.35) targetTop = topEdge;
                else if (relCenter > 0.65) targetTop = bottomEdge;
                else targetTop = centerY;
            }
            else
            {
                // 水平カスタム位置: 中心を保って左右に展開
                targetLeft = ancCenterX - (winW / 2.0);

                double relCenterY = (ancCenterY - workTop) / workH;
                if (relCenterY < 0.35)
                {
                    // 画面上部なら上端固定で下方向に展開
                    targetTop = topEdge;
                }
                else if (relCenterY > 0.65)
                {
                    // 画面下部（タスクバー近傍）なら下端固定で上方向に展開
                    targetTop = bottomEdge;
                }
                else
                {
                    targetTop = ancCenterY - (winH / 2.0);
                }
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

        // 見切れ完全防止: 画面作業領域の範囲内に厳密クランプ
        double minL = Math.Min(leftEdge, rightEdge);
        double maxL = Math.Max(leftEdge, rightEdge);
        targetLeft = Math.Clamp(targetLeft, minL, maxL);

        double minT = Math.Min(topEdge, bottomEdge);
        double maxT = Math.Max(topEdge, bottomEdge);
        targetTop = Math.Clamp(targetTop, minT, maxT);

        return (targetLeft, targetTop, winW, winH);
    }

    /// <summary>アラート表示時のウィンドウRectを計算し、コンパクト時と同じ位置関係を保って返す</summary>
    private (double Left, double Top, double Width, double Height) GetAlertWindowRect(
        OmniGlanceSettings settings, double scale)
    {
        bool isVertical = settings.Orientation == IslandOrientation.Vertical;
        double pillW = isVertical ? 54 : 290;
        double pillH = isVertical ? 146 : 38;
        double winW = (pillW + 32) * scale;
        double winH = (pillH + 32) * scale;

        var sb = GetCurrentScreenBounds(settings);
        double screenW = sb.ScreenWidth;
        double screenH = sb.ScreenHeight;
        double screenLeft = sb.ScreenLeft;
        double screenTop = sb.ScreenTop;
        double workTop = sb.WorkTop;
        double workBottom = sb.WorkBottom;
        double workLeft = sb.WorkLeft;
        double workRight = sb.WorkRight;
        const double vGap = ScreenEdgeVGap;
        const double hGap = ScreenEdgeHGap;

        double leftEdge = workLeft + hGap - (16 * scale);
        double rightEdge = workRight - winW + (16 * scale) - hGap;
        double topEdge = workTop + vGap - (16 * scale);
        double bottomEdge = workBottom - vGap - (winH - 16 * scale);
        double centerX = screenLeft + (screenW - winW) / 2.0;
        double centerY = workTop + (workBottom - workTop - winH) / 2.0;

        double ancLeft = _compactAnchorLeft >= 0 ? _compactAnchorLeft : Left;
        double ancRight = _compactAnchorRight >= 0 ? _compactAnchorRight : (Left + ActualWidth);
        double ancTop = _compactAnchorTop >= 0 ? _compactAnchorTop : Top;
        double ancBottom = _compactAnchorBottom >= 0 ? _compactAnchorBottom : (Top + ActualHeight);
        double ancCenterX = _compactAnchorCenterX >= 0 ? _compactAnchorCenterX : (Left + ActualWidth / 2.0);
        double ancCenterY = _compactAnchorCenterY >= 0 ? _compactAnchorCenterY : (Top + ActualHeight / 2.0);

        double targetLeft, targetTop;

        if (settings.IsCustomPosition)
        {
            if (isVertical)
            {
                double relCenterX = (ancCenterX - screenLeft) / screenW;
                if (relCenterX < 0.35) targetLeft = ancLeft;
                else if (relCenterX > 0.65) targetLeft = ancRight - winW;
                else targetLeft = ancCenterX - winW / 2.0;

                double relCenter = (ancCenterY - screenTop) / screenH;
                if (relCenter < 0.35) targetTop = ancTop;
                else if (relCenter > 0.65) targetTop = ancBottom - winH;
                else targetTop = ancCenterY - winH / 2.0;
            }
            else
            {
                double relCenterX = (ancCenterX - screenLeft) / screenW;
                if (relCenterX < 0.35) targetLeft = ancLeft;
                else if (relCenterX > 0.65) targetLeft = ancRight - winW;
                else targetLeft = ancCenterX - winW / 2.0;

                bool isBottom = ancCenterY > screenTop + screenH / 2.0;
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
        var sb = GetCurrentScreenBounds(_getSettings());
        return Math.Min(sb.ScreenWidth - 40, target);
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
        var sb = GetCurrentScreenBounds(_getSettings());
        return Math.Min(sb.ScreenHeight - 40, target);
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
        var sb = GetCurrentScreenBounds(settings);
        double screenWidth = sb.ScreenWidth;
        double screenHeight = sb.ScreenHeight;
        double screenLeft = sb.ScreenLeft;
        double screenTop = sb.ScreenTop;
        double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;

        double workTop = sb.WorkTop;
        double workBottom = sb.WorkBottom;
        double workLeft = sb.WorkLeft;
        double workRight = sb.WorkRight;

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
                double ancCenterY = _compactAnchorCenterY >= 0 ? _compactAnchorCenterY : (_compactAnchorTop + newWindowHeight / 2.0);
                double relCenterY = (ancCenterY - screenTop) / screenHeight;
                if (relCenterY > 0.65)
                {
                    // 画面下部配置: 下端アンカーを基準にして上方向へ伸縮（タスクバーや画面外への埋もれを防止）
                    targetTop = _compactAnchorBottom >= 0 ? (_compactAnchorBottom - newWindowHeight) : _compactAnchorTop;
                }
                else if (relCenterY >= 0.35)
                {
                    // 画面中央配置: 中心アンカーを基準にして上下に均等伸縮
                    targetTop = ancCenterY - (newWindowHeight / 2.0);
                }
                else
                {
                    // 画面上部配置: 上端アンカーを基準にして下方向へ伸縮
                    targetTop = _compactAnchorTop;
                }
            }
            else
            {
                targetTop = settings.PositionSlot switch
                {
                    IslandPositionSlot.StartStart or IslandPositionSlot.EndStart => workTop + ScreenEdgeVGap - (16 * scale),
                    IslandPositionSlot.StartEnd or IslandPositionSlot.EndEnd => workBottom - ScreenEdgeVGap - (newWindowHeight - 16 * scale),
                    _ => screenTop + (screenHeight - newWindowHeight) / 2.0
                };
            }
            double minTop = workTop + ScreenEdgeVGap - (16 * scale);
            double maxTop = workBottom - ScreenEdgeVGap - (newWindowHeight - 16 * scale);
            targetTop = Math.Clamp(targetTop, minTop, Math.Max(minTop, maxTop));

            if (Math.Abs(currentH - targetH) >= 1.0)
            {
                var anim = new DoubleAnimation(currentH, targetH, duration) { EasingFunction = ease };
                anim.Completed += (s, e) =>
                {
                    IslandPill.Height = targetH;
                    IslandPill.BeginAnimation(HeightProperty, null);
                };
                IslandPill.BeginAnimation(HeightProperty, anim);
            }

            if (Math.Abs(Top - targetTop) >= 1.0)
            {
                var topAnim = new DoubleAnimation(Top, targetTop, duration) { EasingFunction = ease };
                topAnim.Completed += (s, e) =>
                {
                    Top = targetTop;
                    BeginAnimation(TopProperty, null);
                };
                BeginAnimation(TopProperty, topAnim);
            }

            double leftEdgePosition = workLeft + ScreenEdgeHGap - (16 * scale);
            double newWindowWidth = (38 + 32) * scale;
            double rightEdgePosition = workRight - newWindowWidth + (16 * scale) - ScreenEdgeHGap;
            
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
                leftAnim.Completed += (s, e) =>
                {
                    Left = targetLeft;
                    BeginAnimation(LeftProperty, null);
                };
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
                anim.Completed += (s, e) =>
                {
                    IslandPill.Width = targetW;
                    IslandPill.BeginAnimation(WidthProperty, null);
                };
                IslandPill.BeginAnimation(WidthProperty, anim);

                double newWindowWidth = (targetW + 32) * scale;
                double newWindowHeight = (38 + 32) * scale;
                double targetLeft;

                double leftEdgePosition = workLeft + ScreenEdgeHGap - (16 * scale);
                double rightEdgePosition = workRight - newWindowWidth + (16 * scale) - ScreenEdgeHGap;

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
                        _ => screenLeft + (screenWidth - newWindowWidth) / 2.0
                    };
                    _compactAnchorCenterX = targetLeft + (newWindowWidth / 2.0);
                }

                targetLeft = Math.Clamp(targetLeft, leftEdgePosition, Math.Max(leftEdgePosition, rightEdgePosition));

                double topEdge = workTop + ScreenEdgeVGap - (16 * scale);
                double bottomEdge = workBottom - ScreenEdgeVGap - (newWindowHeight - 16 * scale);
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
                leftAnim.Completed += (s, e) =>
                {
                    Left = targetLeft;
                    BeginAnimation(LeftProperty, null);
                };
                BeginAnimation(LeftProperty, leftAnim);

                if (Math.Abs(Top - targetTop) >= 1.0)
                {
                    var topAnim = new DoubleAnimation(Top, targetTop, duration) { EasingFunction = ease };
                    topAnim.Completed += (s, e) =>
                    {
                        Top = targetTop;
                        BeginAnimation(TopProperty, null);
                    };
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
        DateCompactText.Text = now.ToString("M/d");
        DayCompactText.Text = now.ToString("ddd");

        ClockVerticalHourText.Text = now.ToString("HH");
        ClockVerticalMinText.Text = now.ToString("mm");
        DateVerticalText.Text = now.ToString("M/d");
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
            CpuTempLabel.Text = p.CpuTempText;

            GpuProgressBar.Value = p.GpuUsage;
            GpuPercentLabel.Text = p.GpuText;
            GpuTempLabel.Text = p.GpuTempText;

            RamProgressBar.Value = p.RamUsage;
            RamPercentLabel.Text = p.RamText;
            RamDetailLabel.Text = $" ({p.UsedMemoryGb:F1} / {p.TotalMemoryGb:F1} GB)";

            PowerWattsLabel.Text = p.PowerText;
            PowerSourceLabel.Text = $" ({p.PowerSourceText})";

            CheckPerformanceAlerts(p);
        });
    }


    private void CheckPerformanceAlerts(SystemPerformanceInfo p)
    {
        var settings = _getSettings();
        if (!settings.ShowPerformance || _isAlertActive) return;

        var now = DateTime.Now;

        // 1. CPU高温警告
        if (settings.EnableCpuTempAlert && p.CpuTemperature >= settings.CpuTempAlertThreshold)
        {
            if (now - _lastCpuAlertTime > AlertCooldown)
            {
                _lastCpuAlertTime = now;
                TriggerAlert(new IslandAlertInfo
                {
                    Type = IslandAlertType.CpuTemperature,
                    Title = LocalizationService.Instance["OmniGlance_CpuTempAlert"],
                    Message = string.Format(LocalizationService.Instance["OmniGlance_Alert_CpuCritical_Desc"], p.CpuTempText),
                    LevelText = p.CpuTempText,
                    BadgeText = "HOT",
                    Symbol = SymbolRegular.DeveloperBoard20,
                    AlertColor = Color.FromRgb(0xD9, 0x36, 0x3E)
                });
                return;
            }
        }

        // 2. GPU高温警告
        if (settings.EnableGpuTempAlert && p.GpuTemperature >= settings.GpuTempAlertThreshold)
        {
            if (now - _lastGpuAlertTime > AlertCooldown)
            {
                _lastGpuAlertTime = now;
                TriggerAlert(new IslandAlertInfo
                {
                    Type = IslandAlertType.GpuTemperature,
                    Title = LocalizationService.Instance["OmniGlance_GpuTempAlert"],
                    Message = string.Format(LocalizationService.Instance["OmniGlance_Alert_GpuCritical_Desc"], p.GpuTempText),
                    LevelText = p.GpuTempText,
                    BadgeText = "HOT",
                    Symbol = SymbolRegular.WindowDevTools20,
                    AlertColor = Color.FromRgb(0xD9, 0x36, 0x3E)
                });
                return;
            }
        }

        // 3. メモリ使用率警告
        if (settings.EnableMemoryAlert && p.RamUsage >= settings.MemoryAlertThreshold)
        {
            if (now - _lastRamAlertTime > AlertCooldown)
            {
                _lastRamAlertTime = now;
                TriggerAlert(new IslandAlertInfo
                {
                    Type = IslandAlertType.MemoryUsage,
                    Title = LocalizationService.Instance["OmniGlance_MemoryAlert"],
                    Message = string.Format(LocalizationService.Instance["OmniGlance_Alert_MemoryCritical_Desc"], p.RamText),
                    LevelText = p.RamText,
                    BadgeText = "FULL",
                    Symbol = SymbolRegular.Ram20,
                    AlertColor = Color.FromRgb(0xD9, 0x6B, 0x00)
                });
                return;
            }
        }
    }

    private void OnLowBatteryAlert(DeviceBatteryInfo device)
    {
        var settings = _getSettings();
        if (!settings.EnablePulseAnimation && !settings.ShowBattery) return;

        var loc = LocalizationService.Instance;
        string title = device.IsCriticalBattery
            ? loc["OmniGlance_Alert_CriticalTitle"]
            : loc["OmniGlance_Alert_LowTitle"];
        string remainingFormat = loc["OmniGlance_Alert_RemainingFormat"];
        string message = string.Format(remainingFormat, device.Name, device.BatteryLevel);
        string levelText = device.BatteryText;

        Color alertColor = device.IsCriticalBattery ? Color.FromRgb(0xD9, 0x36, 0x3E) : Color.FromRgb(0xD9, 0x6B, 0x00);

        TriggerAlert(new IslandAlertInfo
        {
            Type = device.IsCriticalBattery ? IslandAlertType.CriticalBattery : IslandAlertType.LowBattery,
            Title = title,
            Message = message,
            LevelText = levelText,
            BadgeText = device.IsCriticalBattery ? "CRIT" : "LOW",
            Symbol = device.IsMouse ? SymbolRegular.BatteryWarning24 : device.Symbol,
            IsMouse = device.IsMouse,
            AlertColor = alertColor
        });
    }

    public void TriggerAlert(IslandAlertInfo alert)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var settings = _getSettings();
            _isAlertActive = true;

            // 横表示用更新
            AlertIcon.Symbol = alert.Symbol;
            AlertTitleText.Text = alert.Title;
            AlertMessageText.Text = alert.Message;
            AlertLevelText.Text = alert.LevelText;

            // 縦表示用更新
            AlertVerticalIcon.Symbol = alert.Symbol;
            AlertVerticalTitleText.Text = alert.Title;
            AlertVerticalLevelText.Text = alert.LevelText;
            AlertVerticalBadgeText.Text = alert.BadgeText;

            // アラート背景色（高コントラスト設計: 赤系は#D9363E、警告・アンバー系は#D96B00）
            Color alertBgColor = alert.Type switch
            {
                IslandAlertType.CriticalBattery or IslandAlertType.CpuTemperature or IslandAlertType.GpuTemperature
                    => Color.FromRgb(0xD9, 0x36, 0x3E),
                IslandAlertType.LowBattery or IslandAlertType.MemoryUsage
                    => Color.FromRgb(0xD9, 0x6B, 0x00),
                _ => alert.AlertColor.R > 0xEE && alert.AlertColor.G > 0x80
                    ? Color.FromRgb(0xD9, 0x6B, 0x00)
                    : Color.FromRgb(0xD9, 0x36, 0x3E)
            };

            byte alpha = (byte)Math.Clamp((int)(settings.Opacity * 255), 1, 255);
            IslandPill.Background = new SolidColorBrush(Color.FromArgb(alpha, alertBgColor.R, alertBgColor.G, alertBgColor.B));
            IslandPill.BorderThickness = new Thickness(0);
            IslandPill.BorderBrush = Brushes.Transparent;

            var badgeBgBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));

            // 横表示用スタイル更新
            AlertIconBorder.Background = badgeBgBrush;
            AlertIcon.Foreground = Brushes.White;
            AlertLevelBorder.Background = badgeBgBrush;
            AlertLevelText.Foreground = Brushes.White;

            // 縦表示用スタイル更新
            AlertVerticalIconBorder.Background = badgeBgBrush;
            AlertVerticalIcon.Foreground = Brushes.White;
            AlertVerticalLevelBorder.Background = badgeBgBrush;
            AlertVerticalLevelText.Foreground = Brushes.White;
            AlertVerticalBadgeBorder.Background = badgeBgBrush;
            AlertVerticalBadgeText.Foreground = Brushes.White;

            // 縦表示用デバイスアイコン
            if (alert.IsMouse)
            {
                AlertVerticalMousePath.Visibility = Visibility.Visible;
                AlertVerticalMousePath.Fill = Brushes.White;
                AlertVerticalDeviceIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                AlertVerticalMousePath.Visibility = Visibility.Collapsed;
                AlertVerticalDeviceIcon.Visibility = Visibility.Visible;
                AlertVerticalDeviceIcon.Symbol = alert.Symbol;
                AlertVerticalDeviceIcon.Foreground = Brushes.White;
            }

            AlertGrid.ToolTip = $"{alert.Title}: {alert.Message}";

            // 背景影(DropShadow)は黒のまま維持
            GlowEffect.Color = Colors.Black;

            TransitionToState(IslandState.Alert);

            if (settings.EnableAlertSound)
            {
                OmniGlanceSoundPlayer.PlayAlertSound();
            }

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
                ResetPillBackground();
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

    private void UpdateExpandedLayout()
    {
        if (CalendarViewGrid != null && CalendarViewGrid.Visibility == Visibility.Visible)
        {
            return;
        }

        var settings = _getSettings();
        bool hasAnyPerfItem = settings.ShowCpuUsage || settings.ShowGpuUsage || settings.ShowRamUsage || settings.ShowPowerUsage;
        bool showPerf = settings.ShowPerformance && hasAnyPerfItem;

        int batteryCount = 0;
        if (settings.ShowBattery && _batteryService?.Devices != null)
        {
            batteryCount = _batteryService.Devices.Count(dev =>
                !settings.DisabledDeviceIds.Contains(dev.Id) && !settings.DisabledDeviceIds.Contains(dev.Name));
        }

        bool showRight = settings.ShowBattery && batteryCount > 0;

        if (PerfColumnDef != null && CenterDividerDef != null && RightColumnDef != null)
        {
            if (showPerf && showRight)
            {
                PerfColumnDef.Width = new GridLength(210);
                CenterDividerDef.Width = new GridLength(1);
                RightColumnDef.Width = new GridLength(1, GridUnitType.Star);
                if (CenterDividerRect != null) CenterDividerRect.Visibility = Visibility.Visible;
                if (PerfScrollViewer != null) PerfScrollViewer.Visibility = Visibility.Visible;
                if (RightScrollViewer != null) RightScrollViewer.Visibility = Visibility.Visible;
            }
            else if (showPerf && !showRight)
            {
                PerfColumnDef.Width = new GridLength(1, GridUnitType.Star);
                CenterDividerDef.Width = new GridLength(0);
                RightColumnDef.Width = new GridLength(0);
                if (CenterDividerRect != null) CenterDividerRect.Visibility = Visibility.Collapsed;
                if (PerfScrollViewer != null) PerfScrollViewer.Visibility = Visibility.Visible;
                if (RightScrollViewer != null) RightScrollViewer.Visibility = Visibility.Collapsed;
            }
            else if (!showPerf && showRight)
            {
                PerfColumnDef.Width = new GridLength(0);
                CenterDividerDef.Width = new GridLength(0);
                RightColumnDef.Width = new GridLength(1, GridUnitType.Star);
                if (CenterDividerRect != null) CenterDividerRect.Visibility = Visibility.Collapsed;
                if (PerfScrollViewer != null) PerfScrollViewer.Visibility = Visibility.Collapsed;
                if (RightScrollViewer != null) RightScrollViewer.Visibility = Visibility.Visible;
            }
            else
            {
                PerfColumnDef.Width = new GridLength(1, GridUnitType.Star);
                CenterDividerDef.Width = new GridLength(0);
                RightColumnDef.Width = new GridLength(0);
                if (CenterDividerRect != null) CenterDividerRect.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RecordCompactAnchors()
    {
        double scale = WindowScaleTransform?.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;
        double curW = ActualWidth > 0 ? ActualWidth : ((38 + 32) * scale);
        double curH = ActualHeight > 0 ? ActualHeight : ((38 + 32) * scale);

        _compactAnchorLeft = Left;
        _compactAnchorRight = Left + curW;
        _compactAnchorTop = Top;
        _compactAnchorBottom = Top + curH;
        _compactAnchorCenterX = Left + (curW / 2.0);
        _compactAnchorCenterY = Top + (curH / 2.0);
    }

    private (double width, double height) CalculateExpandedSize()
    {
        var settings = _getSettings();
        var sb = GetCurrentScreenBounds(settings);
        double workW = sb.WorkRight - sb.WorkLeft;
        double workH = sb.WorkBottom - sb.WorkTop;
        double scale = WindowScaleTransform?.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;

        // 見切れ完全防止: 作業領域内に収まる最大許容サイズ
        double maxAllowedW = Math.Max(240, (workW - (ScreenEdgeHGap * 2)) / scale - 32);
        double maxAllowedH = Math.Max(160, (workH - (ScreenEdgeVGap * 2)) / scale - 32);

        if (CalendarViewGrid != null && CalendarViewGrid.Visibility == Visibility.Visible)
        {
            return (Math.Min(560, maxAllowedW), Math.Min(285, maxAllowedH));
        }

        bool hasAnyPerfItem = settings.ShowCpuUsage || settings.ShowGpuUsage || settings.ShowRamUsage || settings.ShowPowerUsage;
        bool showPerf = settings.ShowPerformance && hasAnyPerfItem;

        int batteryCount = 0;
        if (settings.ShowBattery && _batteryService?.Devices != null)
        {
            batteryCount = _batteryService.Devices.Count(dev =>
                !settings.DisabledDeviceIds.Contains(dev.Id) && !settings.DisabledDeviceIds.Contains(dev.Name));
        }

        bool showRight = settings.ShowBattery && batteryCount > 0;

        // 1. 内容量に応じたフレキシブルな横幅 (Width) の決定
        double width;
        if (showPerf && showRight)
        {
            width = batteryCount >= 3 ? 600 : 560;
        }
        else if (showPerf && !showRight)
        {
            int perfItemCount = 0;
            if (settings.ShowCpuUsage) perfItemCount++;
            if (settings.ShowGpuUsage) perfItemCount++;
            if (settings.ShowRamUsage) perfItemCount++;
            if (settings.ShowPowerUsage) perfItemCount++;
            width = perfItemCount > 2 ? 310 : 280;
        }
        else if (!showPerf && showRight)
        {
            width = batteryCount >= 3 ? 420 : 380;
        }
        else
        {
            width = 320;
        }

        // 2. 左カラム (パフォーマンス) の実寸に基づく高さ計算
        double leftHeight = 0;
        if (showPerf)
        {
            if (settings.ShowCpuUsage) leftHeight += 50;
            if (settings.ShowGpuUsage) leftHeight += 50;
            if (settings.ShowRamUsage) leftHeight += 50;
            if (settings.ShowPowerUsage) leftHeight += 40;
            if (leftHeight < 90) leftHeight = 90;
        }

        // 3. 右カラム (バッテリー) の実寸に基づく高さ計算 (スクロール徹底防止)
        double batteryHeight = 0;
        if (showRight)
        {
            // 見出し (CONNECTED DEVICES) 26px + 項目ごとに 42px
            batteryHeight = 26 + (batteryCount * 42);
        }

        double rightHeight = batteryHeight;

        // 4. コンテンツ全体の高さと固定ヘッダー・パディングの合算
        double contentHeight = Math.Max(leftHeight, rightHeight);
        if (contentHeight < 110) contentHeight = 110;

        // ExpandedGrid Margin(上下28) + 上部バー(約36) + 区切り線(11) + 時計追加分 + 余裕バッファ(16)
        bool hasExtraClocks = AdditionalClocksItemsControl != null && AdditionalClocksItemsControl.Visibility == Visibility.Visible;
        double fixedHeaderAndPaddings = (hasExtraClocks ? 96 : 76) + 16;

        double targetHeight = fixedHeaderAndPaddings + contentHeight;

        // 5. 画面領域オーバーフロー防止 (完全クランプ)
        if (targetHeight > maxAllowedH) targetHeight = maxAllowedH;
        if (width > maxAllowedW) width = maxAllowedW;

        return (width, targetHeight);
    }

    private bool _isAutoFitting = false;

    private void AutoFitExpandedSizeIfNeeded()
    {
        if (_currentState != IslandState.Expanded || _isAutoFitting) return;

        Dispatcher.InvokeAsync(() =>
        {
            if (_currentState != IslandState.Expanded || _isAutoFitting) return;

            try
            {
                _isAutoFitting = true;
                double scrollLeft = PerfScrollViewer != null ? PerfScrollViewer.ScrollableHeight : 0;
                double scrollRight = RightScrollViewer != null ? RightScrollViewer.ScrollableHeight : 0;
                double neededExtra = Math.Max(scrollLeft, scrollRight);

                if (neededExtra > 0.5)
                {
                    var settings = _getSettings();
                    var sb = GetCurrentScreenBounds(settings);
                    double workH = sb.WorkBottom - sb.WorkTop;
                    double scale = WindowScaleTransform?.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;
                    double maxAllowedH = Math.Max(160, (workH - (ScreenEdgeVGap * 2)) / scale - 32);

                    double currentH = IslandPill.ActualHeight > 0 ? IslandPill.ActualHeight : IslandPill.Height;
                    double newTargetH = Math.Min(currentH + neededExtra + 8, maxAllowedH);

                    if (newTargetH > currentH + 1.0)
                    {
                        AnimatePillToSize(IslandPill.Width, newTargetH);
                    }
                }
            }
            finally
            {
                _isAutoFitting = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private void AnimatePillToSize(double targetW, double targetH)
    {
        var settings = _getSettings();
        double scale = WindowScaleTransform?.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;

        // 見切れ完全防止: 新しい幅・高さでクランプ済み展開ウィンドウ座標を再計算
        var (expLeft, expTop, _, _) = GetExpandedWindowRect(settings, scale, targetW, targetH);

        var easeOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var mainDuration = TimeSpan.FromMilliseconds(260);

        var widthAnim = new DoubleAnimation(IslandPill.ActualWidth > 0 ? IslandPill.ActualWidth : targetW, targetW, mainDuration) { EasingFunction = easeOut };
        var heightAnim = new DoubleAnimation(IslandPill.ActualHeight > 0 ? IslandPill.ActualHeight : targetH, targetH, mainDuration) { EasingFunction = easeOut };
        var leftAnim = new DoubleAnimation(Left, expLeft, mainDuration) { EasingFunction = easeOut };
        var topAnim = new DoubleAnimation(Top, expTop, mainDuration) { EasingFunction = easeOut };

        widthAnim.Completed += (s, e) =>
        {
            IslandPill.Width = targetW;
            IslandPill.BeginAnimation(WidthProperty, null);
        };
        heightAnim.Completed += (s, e) =>
        {
            IslandPill.Height = targetH;
            IslandPill.BeginAnimation(HeightProperty, null);
            AutoFitExpandedSizeIfNeeded();
        };
        leftAnim.Completed += (s, e) =>
        {
            Left = expLeft;
            BeginAnimation(LeftProperty, null);
        };
        topAnim.Completed += (s, e) =>
        {
            Top = expTop;
            BeginAnimation(TopProperty, null);
        };

        IslandPill.BeginAnimation(WidthProperty, widthAnim);
        IslandPill.BeginAnimation(HeightProperty, heightAnim);
        BeginAnimation(LeftProperty, leftAnim);
        BeginAnimation(TopProperty, topAnim);
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
                UpdateExpandedLayout();
                (targetWidth, targetHeight) = CalculateExpandedSize();
                targetCornerRadius = new CornerRadius(26);
                incomingGrid = ExpandedGrid;
                break;

            case IslandState.Alert:
                bool isVertAlert = IsVerticalMode;
                AlertHorizontalPanel.Visibility = !isVertAlert ? Visibility.Visible : Visibility.Collapsed;
                AlertVerticalPanel.Visibility = isVertAlert ? Visibility.Visible : Visibility.Collapsed;
                if (isVertAlert)
                {
                    targetWidth = 54;
                    targetHeight = 146;
                    targetCornerRadius = new CornerRadius(22);
                }
                else
                {
                    targetWidth = 290;
                    targetHeight = 38;
                    targetCornerRadius = new CornerRadius(19);
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

        widthAnim.Completed += (s, e) =>
        {
            if (gen != _transitionGeneration) return;
            IslandPill.Width = targetWidth;
            IslandPill.BeginAnimation(WidthProperty, null);
        };
        heightAnim.Completed += (s, e) =>
        {
            if (gen != _transitionGeneration) return;
            IslandPill.Height = targetHeight;
            IslandPill.BeginAnimation(HeightProperty, null);
            if (targetState == IslandState.Expanded)
            {
                AutoFitExpandedSizeIfNeeded();
            }
        };

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

        if (targetState != IslandState.Alert)
        {
            ResetPillBackground();
        }

        if (targetState == IslandState.Compact)
        {
            // コンパクトに戻る → 元のアンカー位置へ (画面外クランプ済み)
            double targetLeft = _compactAnchorLeft >= 0 ? _compactAnchorLeft : Left;
            double targetTop = _compactAnchorTop >= 0 ? _compactAnchorTop : Top;

            var sb = GetCurrentScreenBounds(settings);
            double curWinH = (targetHeight + 32) * scale;
            double minTop = sb.WorkTop + ScreenEdgeVGap - (16 * scale);
            double maxTop = sb.WorkBottom - ScreenEdgeVGap - (curWinH - 16 * scale);
            targetTop = Math.Clamp(targetTop, minTop, Math.Max(minTop, maxTop));

            var leftBack = new DoubleAnimation(Left, targetLeft, mainDuration) { EasingFunction = easeOut };
            var topBack = new DoubleAnimation(Top, targetTop, mainDuration) { EasingFunction = easeOut };
            leftBack.Completed += (s, e) =>
            {
                if (gen != _transitionGeneration) return;
                Left = targetLeft;
                BeginAnimation(LeftProperty, null);
            };
            topBack.Completed += (s, e) =>
            {
                if (gen != _transitionGeneration) return;
                Top = targetTop;
                BeginAnimation(TopProperty, null);
            };
            BeginAnimation(LeftProperty, leftBack);
            BeginAnimation(TopProperty, topBack);
        }
        else if (targetState == IslandState.Alert)
        {
            // アラート → GetAlertWindowRect で計算（位置ズレ防止）
            var (alertLeft, alertTop, _, _) = GetAlertWindowRect(settings, scale);
            var leftAnim = new DoubleAnimation(Left, alertLeft, mainDuration) { EasingFunction = easeOut };
            var topAnim = new DoubleAnimation(Top, alertTop, mainDuration) { EasingFunction = easeOut };
            leftAnim.Completed += (s, e) =>
            {
                if (gen != _transitionGeneration) return;
                Left = alertLeft;
                BeginAnimation(LeftProperty, null);
            };
            topAnim.Completed += (s, e) =>
            {
                if (gen != _transitionGeneration) return;
                Top = alertTop;
                BeginAnimation(TopProperty, null);
            };
            BeginAnimation(LeftProperty, leftAnim);
            BeginAnimation(TopProperty, topAnim);
        }
        else
        {
            // 展開 → GetExpandedWindowRect で動的サイズから厳密計算
            var (expLeft, expTop, _, _) = GetExpandedWindowRect(settings, scale, targetWidth, targetHeight);
            var leftAnim = new DoubleAnimation(Left, expLeft, mainDuration) { EasingFunction = easeOut };
            var topAnim = new DoubleAnimation(Top, expTop, mainDuration) { EasingFunction = easeOut };
            leftAnim.Completed += (s, e) =>
            {
                if (gen != _transitionGeneration) return;
                Left = expLeft;
                BeginAnimation(LeftProperty, null);
            };
            topAnim.Completed += (s, e) =>
            {
                if (gen != _transitionGeneration) return;
                Top = expTop;
                BeginAnimation(TopProperty, null);
            };
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
            RecordCompactAnchors();
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

                double scale = WindowScaleTransform.ScaleX > 0 ? WindowScaleTransform.ScaleX : 1.0;
                double curPillW = IsVerticalMode ? 38 : (IslandPill.ActualWidth > 0 ? IslandPill.ActualWidth : CalculateCompactWidth());
                double curPillH = IsVerticalMode ? (IslandPill.ActualHeight > 0 ? IslandPill.ActualHeight : CalculateCompactHeight()) : 38;
                double totalW = (curPillW + 32) * scale;
                double curH = (curPillH + 32) * scale;

                // ドラッグ完了時のウィンドウ中心点から配置先モニターを特定
                double centerX = Left + (totalW / 2.0);
                double centerY = Top + (curH / 2.0);
                var sb = OmniScreenHelper.FindScreenAtDipPoint(centerX, centerY, settings.AllowTaskbarPlacement);
                settings.TargetMonitorDeviceName = sb.DeviceName;

                double leftEdge = sb.WorkLeft + ScreenEdgeHGap - (16 * scale);
                double rightEdge = sb.WorkRight - totalW + (16 * scale) - ScreenEdgeHGap;
                double topEdge = sb.WorkTop + ScreenEdgeVGap - (16 * scale);
                double bottomEdge = sb.WorkBottom - ScreenEdgeVGap - (curH - 16 * scale);

                Left = Math.Clamp(Left, leftEdge, Math.Max(leftEdge, rightEdge));
                Top = Math.Clamp(Top, topEdge, Math.Max(topEdge, bottomEdge));

                // 実際のドラッグ完了後にのみカスタム座標を保存・アンカーを更新
                settings.IsCustomPosition = true;
                settings.CustomLeft = Left;
                settings.CustomTop = Top;
                _saveSettings(settings);

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
                RecordCompactAnchors();
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
                ResetPillBackground();
                _hoverCollapseTimer?.Stop();
                _isExpanded = true;
                RecordCompactAnchors();
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
            if (_isExpanded)
            {
                RecordCompactAnchors();
            }
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

        // 画面最前面に表示
        var alwaysOnTopItem = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_Menu_AlwaysOnTop"],
            IsCheckable = true,
            IsChecked = settings.AlwaysOnTop,
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.PositionToFront24, FontSize = 16 }
        };
        alwaysOnTopItem.Click += (s, e) =>
        {
            settings.AlwaysOnTop = alwaysOnTopItem.IsChecked;
            _saveSettings(settings);
            ApplyAlwaysOnTop(settings.AlwaysOnTop);
        };
        menu.Items.Add(alwaysOnTopItem);

        // 5. 表示モード サブメニュー
        var orientSubMenu = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_DisplayMode"],
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.Orientation24, FontSize = 16 }
        };

        var horizItem = new System.Windows.Controls.MenuItem
        {
            Header = loc["OmniGlance_Mode_Horizontal"],
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
            Header = loc["OmniGlance_Mode_Vertical"],
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
                (IslandPositionSlot.StartStart, loc["OmniGlance_Slot_TopLeft"]),
                (IslandPositionSlot.StartCenter, loc["OmniGlance_Slot_Left"]),
                (IslandPositionSlot.StartEnd, loc["OmniGlance_Slot_BottomLeft"]),
                (IslandPositionSlot.EndStart, loc["OmniGlance_Slot_TopRight"]),
                (IslandPositionSlot.EndCenter, loc["OmniGlance_Slot_Right"]),
                (IslandPositionSlot.EndEnd, loc["OmniGlance_Slot_BottomRight"])
            };
        }
        else
        {
            slots = new (IslandPositionSlot, string)[]
            {
                (IslandPositionSlot.StartStart, loc["OmniGlance_Slot_TopLeft"]),
                (IslandPositionSlot.CenterStart, loc["OmniGlance_Slot_TopCenter"]),
                (IslandPositionSlot.EndStart, loc["OmniGlance_Slot_TopRight"]),
                (IslandPositionSlot.CenterEnd, loc["OmniGlance_Slot_BottomCenter"]),
                (IslandPositionSlot.StartEnd, loc["OmniGlance_Slot_BottomLeft"]),
                (IslandPositionSlot.EndEnd, loc["OmniGlance_Slot_BottomRight"])
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

        UpdateCalendarIndicator();

        if (_currentState == IslandState.Expanded)
        {
            UpdateExpandedLayout();
            var (w, h) = CalculateExpandedSize();
            AnimatePillToSize(w, h);
        }
    }

    private void OnCalendarBadgeClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (CalendarViewGrid.Visibility != Visibility.Visible)
        {
            OnCalendarToggleClicked(sender, new RoutedEventArgs());
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
        var culture = System.Globalization.CultureInfo.GetCultureInfo(LocalizationService.Instance.EffectiveLanguageCode);
        CalendarMonthTitleText.Text = targetMonth.ToString("Y", culture);

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
        var culture = System.Globalization.CultureInfo.GetCultureInfo(LocalizationService.Instance.EffectiveLanguageCode);
        SelectedDateHeaderText.Text = $"{date.ToString("M", culture)} ({date.ToString("ddd", culture)})";

        var evs = _calendarService?.GetEventsForDate(date) ?? new List<CalendarEvent>();
        DateEventsItemsControl.ItemsSource = evs;
        NoEventsPlaceholder.Visibility = evs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_calendarService != null)
        {
            CalendarSyncStatusText.Text = $"{LocalizationService.Instance["OmniGlance_CalendarStatus"]}: {_calendarService.LastSyncStatus}";
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
            PinBtn.ToolTip = LocalizationService.Instance["OmniGlance_Pin_Tooltip"];
            PinBtnIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
        }
        else
        {
            PinBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent;
            PinBtn.ToolTip = LocalizationService.Instance["OmniGlance_Pin_Tooltip"];
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
        var settings = _getSettings();

        // 時計表示の有無と後続要素の有無に応じたマージン最適化
        bool hasAnyPerfItem = settings.ShowCpuUsage || settings.ShowGpuUsage || settings.ShowRamUsage || settings.ShowPowerUsage;
        bool hasTrailingItem = settings.ShowClock
            || (settings.ShowPerformance && hasAnyPerfItem)
            || (settings.ShowBattery && _batteryService.Devices.Any(d =>
                !settings.DisabledDeviceIds.Contains(d.Id) && !settings.DisabledDeviceIds.Contains(d.Name)));

        if (settings.ShowClock)
        {
            CalendarIndicatorCompact.Margin = new Thickness(0, 0, 6, 0);
            CalendarIndicatorVertical.Margin = new Thickness(0, 0, 0, 4);
        }
        else if (hasTrailingItem)
        {
            CalendarIndicatorCompact.Margin = new Thickness(0, 0, 10, 0);
            CalendarIndicatorVertical.Margin = new Thickness(0, 0, 0, 8);
        }
        else
        {
            CalendarIndicatorCompact.Margin = new Thickness(0);
            CalendarIndicatorVertical.Margin = new Thickness(0);
        }

        if (_calendarService == null)
        {
            CalendarIndicatorCompact.Visibility = Visibility.Collapsed;
            CalendarIndicatorVertical.Visibility = Visibility.Collapsed;
            CalendarExpandedBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var todayEvents = _calendarService.GetEventsForDate(DateTime.Today);
        if (todayEvents.Count > 0)
        {
            var firstEvent = todayEvents.FirstOrDefault(e => e.EndTime > DateTime.Now) ?? todayEvents[0];
            var brush = firstEvent.ColorBrush;
            string countText = string.Format(LocalizationService.Instance["OmniGlance_EventCount_Format"], todayEvents.Count);
            string headerText = LocalizationService.Instance["OmniGlance_TodayEvents_Tooltip"].Split('(')[0].Trim();
            string eventTooltip = $"{headerText} ({countText})\n・{firstEvent.Title} ({firstEvent.TimeText})";

            CalendarIndicatorCompact.Background = brush;
            CalendarIndicatorCompact.Visibility = Visibility.Visible;
            CalendarIndicatorCompact.ToolTip = eventTooltip;

            CalendarIndicatorVertical.Background = brush;
            CalendarIndicatorVertical.Visibility = Visibility.Visible;
            CalendarIndicatorVertical.ToolTip = eventTooltip;

            // 展開時 カレンダーではないほう（通常ステータス表示時）の日付横バッジ
            if (CalendarViewGrid.Visibility != Visibility.Visible)
            {
                if (brush != null)
                {
                    CalendarExpandedBadge.Background = brush;
                }
                else if (TryFindResource("AccentFillColorDefaultBrush") is Brush accentBrush)
                {
                    CalendarExpandedBadge.Background = accentBrush;
                }
                CalendarExpandedBadgeText.Text = countText;
                CalendarExpandedBadge.ToolTip = $"{eventTooltip}\n{LocalizationService.Instance["OmniGlance_TodayEvents_Tooltip"]}";
                CalendarExpandedBadge.Visibility = Visibility.Visible;
            }
            else
            {
                CalendarExpandedBadge.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            CalendarIndicatorCompact.Visibility = Visibility.Collapsed;
            CalendarIndicatorVertical.Visibility = Visibility.Collapsed;
            CalendarExpandedBadge.Visibility = Visibility.Collapsed;
        }

        UpdateCompactSizeSmoothly();
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
                string.Format(LocalizationService.Instance["Calendar_ConfirmDelete_Msg"], ev.Title),
                LocalizationService.Instance["Calendar_ConfirmDelete_Title"],
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
