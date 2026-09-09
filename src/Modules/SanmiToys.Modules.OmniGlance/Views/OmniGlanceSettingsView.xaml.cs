using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SanmiToys.Modules.OmniGlance.Models;
using SanmiToys.Modules.OmniGlance.Services;

namespace SanmiToys.Modules.OmniGlance.Views;

public partial class OmniGlanceSettingsView : UserControl
{
    private readonly OmniGlanceModule _module;
    private bool _isInitializing = true;
    private System.Collections.ObjectModel.ObservableCollection<CalendarSubscription> _calendarSubs = new();
    private static readonly string[] _presetColors = new[]
    {
        "#4CC2FF", // スカイブルー
        "#FF6B6B", // コーラルレッド
        "#52C41A", // エメラルドグリーン
        "#FFA940", // アンバーオレンジ
        "#9254DE", // パープル
        "#FF85C0", // ピンク
        "#36CFC9", // シアン
        "#FFD666"  // イエロー
    };

    public OmniGlanceSettingsView(OmniGlanceModule module)
    {
        _module = module;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = _module.Settings;

        EnableSwitch.IsChecked = _module.IsEnabled;

        UpdatePositionUi();
        ColorModeRadio.IsChecked = s.ColorMode == IslandColorMode.Color;
        MonochromeModeRadio.IsChecked = s.ColorMode == IslandColorMode.Monochrome;
        ShowBadgeBackgroundSwitch.IsChecked = s.ShowBadgeBackground;
        LockPositionSwitch.IsChecked = s.IsPositionLocked;
        AllowTaskbarPlacementSwitch.IsChecked = s.AllowTaskbarPlacement;
        ScaleSlider.Value = s.IslandScale * 100.0;
        ScaleText.Text = $"{(int)(s.IslandScale * 100)}%";
        AutoExpandSwitch.IsChecked = s.AutoExpandOnHover;
        ClickThroughSwitch.IsChecked = s.IsClickThrough;
        OpacitySlider.Value = s.Opacity * 100.0;
        OpacityText.Text = $"{(int)(s.Opacity * 100)}%";

        ShowClockSwitch.IsChecked = s.ShowClock;
        ShowPerfSwitch.IsChecked = s.ShowPerformance;
        PerfSubOptionsPanel.Visibility = s.ShowPerformance ? Visibility.Visible : Visibility.Collapsed;
        ShowCpuCheckBox.IsChecked = s.ShowCpuUsage;
        ShowGpuCheckBox.IsChecked = s.ShowGpuUsage;
        ShowRamCheckBox.IsChecked = s.ShowRamUsage;
        ShowPowerCheckBox.IsChecked = s.ShowPowerUsage;
        ShowBatterySwitch.IsChecked = s.ShowBattery;
        BatterySubOptionsPanel.Visibility = s.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
        BatterySubOptionsItemsControl.ItemsSource = _module.BatteryService?.Devices;
        UpdateBatterySubOptionsVisibility();
        if (_module.BatteryService != null)
        {
            _module.BatteryService.Devices.CollectionChanged += (sender, args) =>
            {
                Dispatcher.InvokeAsync(UpdateBatterySubOptionsVisibility);
            };
        }

        LowThresholdSlider.Value = s.LowBatteryThreshold;
        LowThresholdText.Text = $"{s.LowBatteryThreshold}%";

        CriticalThresholdSlider.Value = s.CriticalBatteryThreshold;
        CriticalThresholdText.Text = $"{s.CriticalBatteryThreshold}%";

        PulseAnimSwitch.IsChecked = s.EnablePulseAnimation;

        // パフォーマンス警告設定
        EnableCpuTempAlertSwitch.IsChecked = s.EnableCpuTempAlert;
        CpuTempThresholdSlider.Value = s.CpuTempAlertThreshold;
        CpuTempThresholdText.Text = $"{s.CpuTempAlertThreshold}°C";

        EnableGpuTempAlertSwitch.IsChecked = s.EnableGpuTempAlert;
        GpuTempThresholdSlider.Value = s.GpuTempAlertThreshold;
        GpuTempThresholdText.Text = $"{s.GpuTempAlertThreshold}°C";

        EnableMemoryAlertSwitch.IsChecked = s.EnableMemoryAlert;
        MemoryThresholdSlider.Value = s.MemoryAlertThreshold;
        MemoryThresholdText.Text = $"{s.MemoryAlertThreshold}%";

        DemoDevicesSwitch.IsChecked = s.EnableDemoDevices;

        // 後方互換移行
        if (s.CalendarSubscriptions.Count == 0 && !string.IsNullOrWhiteSpace(s.GoogleCalendarIcalUrl))
        {
            s.CalendarSubscriptions.Add(new CalendarSubscription
            {
                Name = "Google カレンダー",
                Url = s.GoogleCalendarIcalUrl,
                ColorHex = "#4CC2FF",
                IsEnabled = true
            });
        }

        // Google カレンダー連携
        GoogleSyncSwitch.IsChecked = s.GoogleSyncEnabled;
        GoogleClientIdBox.Text = s.GoogleClientId;
        GoogleClientSecretBox.Text = s.GoogleClientSecret;
        UpdateGoogleAccountUi();

        // iPhone / iCloud カレンダー連携
        ICloudSyncSwitch.IsChecked = s.ICloudSyncEnabled;
        ICloudAppleIdBox.Text = s.ICloudAppleId;
        ICloudAppPwBox.Text = SecurityHelper.DecryptString(s.ICloudAppSpecificPasswordEncrypted);
        ICloudCalendarNameBox.Text = s.ICloudCalendarName;
        UpdateICloudAccountUi();

        _calendarSubs = new System.Collections.ObjectModel.ObservableCollection<CalendarSubscription>(s.CalendarSubscriptions);
        CalendarsItemsControl.ItemsSource = _calendarSubs;

        UpdateCalendarStatus();

        _isInitializing = false;
    }

    private void Save()
    {
        if (_isInitializing) return;
        _module.SaveSettings();
        _module.UpdateOverlay();
    }

    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.IsEnabled = EnableSwitch.IsChecked ?? false;
    }

    private bool _isUpdatingUi = false;

    private void UpdatePositionUi()
    {
        _isUpdatingUi = true;
        try
        {
            var s = _module.Settings;
            bool isVert = s.Orientation == IslandOrientation.Vertical;

            HorizontalModeRadio.IsChecked = !isVert;
            VerticalModeRadio.IsChecked = isVert;

            HorizontalSlotsGrid.Visibility = !isVert ? Visibility.Visible : Visibility.Collapsed;
            VerticalSlotsGrid.Visibility = isVert ? Visibility.Visible : Visibility.Collapsed;

            if (!isVert)
            {
                SlotH_StartStart.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.StartStart);
                SlotH_CenterStart.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.CenterStart);
                SlotH_EndStart.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.EndStart);
                SlotH_StartEnd.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.StartEnd);
                SlotH_CenterEnd.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.CenterEnd);
                SlotH_EndEnd.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.EndEnd);
            }
            else
            {
                SlotV_StartStart.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.StartStart);
                SlotV_StartCenter.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.StartCenter);
                SlotV_StartEnd.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.StartEnd);
                SlotV_EndStart.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.EndStart);
                SlotV_EndCenter.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.EndCenter);
                SlotV_EndEnd.IsChecked = (!s.IsCustomPosition && s.PositionSlot == IslandPositionSlot.EndEnd);
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void OnOrientationChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isUpdatingUi) return;

        bool isVert;
        if (sender == VerticalModeRadio)
        {
            isVert = true;
        }
        else if (sender == HorizontalModeRadio)
        {
            isVert = false;
        }
        else
        {
            isVert = VerticalModeRadio.IsChecked == true;
        }

        // 縦横切り替え: 各モードの前回保存位置（スロットまたはカスタム位置）が自動復元される
        _module.Settings.Orientation = isVert ? IslandOrientation.Vertical : IslandOrientation.Horizontal;
        _module.Settings.HasMigratedToSlots = true;

        UpdatePositionUi();
        Save();
    }

    private void OnColorModeChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isUpdatingUi) return;
        _module.Settings.ColorMode = (ColorModeRadio.IsChecked == true)
            ? IslandColorMode.Color
            : IslandColorMode.Monochrome;
        Save();
    }

    private void OnShowBadgeBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isUpdatingUi) return;
        _module.Settings.ShowBadgeBackground = ShowBadgeBackgroundSwitch.IsChecked ?? true;
        Save();
    }

    private void OnSlotCheckboxClicked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isUpdatingUi) return;

        if (sender == SlotH_StartStart || sender == SlotV_StartStart)
            _module.Settings.PositionSlot = IslandPositionSlot.StartStart;
        else if (sender == SlotH_CenterStart)
            _module.Settings.PositionSlot = IslandPositionSlot.CenterStart;
        else if (sender == SlotH_EndStart || sender == SlotV_EndStart)
            _module.Settings.PositionSlot = IslandPositionSlot.EndStart;
        else if (sender == SlotH_StartEnd || sender == SlotV_StartEnd)
            _module.Settings.PositionSlot = IslandPositionSlot.StartEnd;
        else if (sender == SlotH_CenterEnd)
            _module.Settings.PositionSlot = IslandPositionSlot.CenterEnd;
        else if (sender == SlotH_EndEnd || sender == SlotV_EndEnd)
            _module.Settings.PositionSlot = IslandPositionSlot.EndEnd;
        else if (sender == SlotV_StartCenter)
            _module.Settings.PositionSlot = IslandPositionSlot.StartCenter;
        else if (sender == SlotV_EndCenter)
            _module.Settings.PositionSlot = IslandPositionSlot.EndCenter;

        _module.Settings.IsCustomPosition = false;
        _module.Settings.CustomLeft = -1;
        _module.Settings.CustomTop = -1;
        _module.Settings.HasMigratedToSlots = true;

        UpdatePositionUi();
        Save();
    }

    private void OnLockPositionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.IsPositionLocked = LockPositionSwitch.IsChecked ?? false;
        Save();
    }

    private void OnAllowTaskbarPlacementChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.AllowTaskbarPlacement = AllowTaskbarPlacementSwitch.IsChecked ?? false;
        Save();
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)ScaleSlider.Value;
        ScaleText.Text = $"{val}%";
        _module.Settings.IslandScale = val / 100.0;
        Save();
    }

    private void OnAutoExpandChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.AutoExpandOnHover = AutoExpandSwitch.IsChecked ?? true;
        Save();
    }

    private void OnClickThroughChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.IsClickThrough = ClickThroughSwitch.IsChecked ?? false;
        Save();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)OpacitySlider.Value;
        OpacityText.Text = $"{val}%";
        _module.Settings.Opacity = val / 100.0;
        Save();
    }

    private void OnShowClockChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.ShowClock = ShowClockSwitch.IsChecked ?? true;
        Save();
    }

    private void OnShowPerfChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool isPerfEnabled = ShowPerfSwitch.IsChecked ?? true;
        _module.Settings.ShowPerformance = isPerfEnabled;
        PerfSubOptionsPanel.Visibility = isPerfEnabled ? Visibility.Visible : Visibility.Collapsed;
        Save();
    }

    private void OnPerfSubOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.ShowCpuUsage = ShowCpuCheckBox.IsChecked ?? true;
        _module.Settings.ShowGpuUsage = ShowGpuCheckBox.IsChecked ?? true;
        _module.Settings.ShowRamUsage = ShowRamCheckBox.IsChecked ?? true;
        _module.Settings.ShowPowerUsage = ShowPowerCheckBox.IsChecked ?? true;
        Save();
    }

    private void UpdateBatterySubOptionsVisibility()
    {
        var devs = _module.BatteryService?.Devices;
        bool hasDevs = devs != null && devs.Count > 0;
        NoDevicesDetectedText.Visibility = hasDevs ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnShowBatteryChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.ShowBattery = ShowBatterySwitch.IsChecked ?? true;
        BatterySubOptionsPanel.Visibility = _module.Settings.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
        Save();
    }

    private void OnBatteryDeviceOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var devs = _module.BatteryService?.Devices;
        if (devs == null) return;

        _module.Settings.DisabledDeviceIds.Clear();
        foreach (var dev in devs)
        {
            if (!dev.IsOptionEnabled)
            {
                if (!string.IsNullOrWhiteSpace(dev.Id)) _module.Settings.DisabledDeviceIds.Add(dev.Id);
                if (!string.IsNullOrWhiteSpace(dev.Name) && !_module.Settings.DisabledDeviceIds.Contains(dev.Name))
                {
                    _module.Settings.DisabledDeviceIds.Add(dev.Name);
                }
            }
        }
        Save();
    }

    private void OnLowThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)LowThresholdSlider.Value;
        LowThresholdText.Text = $"{val}%";
        _module.Settings.LowBatteryThreshold = val;
        Save();
    }

    private void OnCriticalThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)CriticalThresholdSlider.Value;
        CriticalThresholdText.Text = $"{val}%";
        _module.Settings.CriticalBatteryThreshold = val;
        Save();
    }

    private void OnPulseAnimChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.EnablePulseAnimation = PulseAnimSwitch.IsChecked ?? true;
        Save();
    }

    private void OnEnableCpuTempAlertChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.EnableCpuTempAlert = EnableCpuTempAlertSwitch.IsChecked ?? true;
        Save();
    }

    private void OnCpuTempThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)CpuTempThresholdSlider.Value;
        CpuTempThresholdText.Text = $"{val}°C";
        _module.Settings.CpuTempAlertThreshold = val;
        Save();
    }

    private void OnEnableGpuTempAlertChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.EnableGpuTempAlert = EnableGpuTempAlertSwitch.IsChecked ?? true;
        Save();
    }

    private void OnGpuTempThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)GpuTempThresholdSlider.Value;
        GpuTempThresholdText.Text = $"{val}°C";
        _module.Settings.GpuTempAlertThreshold = val;
        Save();
    }

    private void OnEnableMemoryAlertChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.EnableMemoryAlert = EnableMemoryAlertSwitch.IsChecked ?? true;
        Save();
    }

    private void OnMemoryThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)MemoryThresholdSlider.Value;
        MemoryThresholdText.Text = $"{val}%";
        _module.Settings.MemoryAlertThreshold = val;
        Save();
    }

    private void OnDemoDevicesChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.EnableDemoDevices = DemoDevicesSwitch.IsChecked ?? true;
        Save();
        _ = _module.RefreshBatteryScanAsync();
    }

    private void OnTestAlertClicked(object sender, RoutedEventArgs e)
    {
        _module.TriggerTestBatteryAlert();
    }

    private void OnTestBatteryAlertClicked(object sender, RoutedEventArgs e)
    {
        _module.TriggerTestBatteryAlert();
    }

    private void OnTestCpuAlertClicked(object sender, RoutedEventArgs e)
    {
        _module.TriggerTestCpuTempAlert();
    }

    private void OnTestGpuAlertClicked(object sender, RoutedEventArgs e)
    {
        _module.TriggerTestGpuTempAlert();
    }

    private void OnTestMemoryAlertClicked(object sender, RoutedEventArgs e)
    {
        _module.TriggerTestMemoryAlert();
    }

    private void OnAddCalendarClicked(object sender, RoutedEventArgs e)
    {
        string color = _presetColors[_calendarSubs.Count % _presetColors.Length];
        var newSub = new CalendarSubscription
        {
            Name = $"カレンダー {_calendarSubs.Count + 1}",
            Url = string.Empty,
            ColorHex = color,
            IsEnabled = true
        };
        _calendarSubs.Add(newSub);
        _module.Settings.CalendarSubscriptions = _calendarSubs.ToList();
        Save();
    }

    private void OnCalendarColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CalendarSubscription sub)
        {
            // 次のプリセットカラーに循環切り替え
            int curIdx = Array.IndexOf(_presetColors, sub.ColorHex);
            int nextIdx = (curIdx + 1) % _presetColors.Length;
            sub.ColorHex = _presetColors[nextIdx];
            _module.Settings.CalendarSubscriptions = _calendarSubs.ToList();
            Save();
            _ = _module.SyncCalendarAsync();
        }
    }

    private void OnCalendarItemChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.CalendarSubscriptions = _calendarSubs.ToList();
        Save();
        _ = _module.SyncCalendarAsync();
    }

    private void OnCalendarItemNameOrUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.CalendarSubscriptions = _calendarSubs.ToList();
        Save();
    }

    private void OnDeleteCalendarClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is CalendarSubscription sub)
        {
            _calendarSubs.Remove(sub);
            _module.Settings.CalendarSubscriptions = _calendarSubs.ToList();
            Save();
            _ = _module.SyncCalendarAsync();
        }
    }

    private async void OnCalendarSyncNowClicked(object sender, RoutedEventArgs e)
    {
        CalendarSyncNowBtn.IsEnabled = false;
        CalendarStatusText.Text = "同期中...";

        await _module.SyncCalendarAsync();
        UpdateCalendarStatus();

        CalendarSyncNowBtn.IsEnabled = true;
    }

    private void UpdateCalendarStatus()
    {
        CalendarStatusText.Text = _module.CalendarService?.LastSyncStatus ?? "未同期";
        UpdateGoogleAccountUi();
        UpdateICloudAccountUi();
    }

    private void OnGoogleSyncChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.GoogleSyncEnabled = GoogleSyncSwitch.IsChecked ?? false;
        Save();
        _ = _module.SyncCalendarAsync();
    }

    private void OnGoogleCredentialsChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.GoogleClientId = GoogleClientIdBox.Text.Trim();
        _module.Settings.GoogleClientSecret = GoogleClientSecretBox.Text.Trim();
        Save();
    }


    private void OnOpenGoogleCalendarWebClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://calendar.google.com/calendar/u/0/r/settings");
    }

    private async void OnGoogleLoginClicked(object sender, RoutedEventArgs e)
    {
        string cid = GoogleClientIdBox.Text.Trim();
        string csecret = GoogleClientSecretBox.Text.Trim();

        GoogleLoginBtn.IsEnabled = false;
        GoogleLoginResultText.Text = "ブラウザを開いて認証を待機しています...";
        GoogleLoginResultText.Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush") ?? Brushes.DodgerBlue;
        GoogleLoginResultText.Visibility = Visibility.Visible;

        if (_module.CalendarService != null)
        {
            var (success, msg) = await _module.CalendarService.GoogleAuth.SignInAsync(
                string.IsNullOrWhiteSpace(cid) ? null : cid,
                string.IsNullOrWhiteSpace(csecret) ? null : csecret);

            if (success)
            {
                GoogleLoginResultText.Text = msg;
                GoogleLoginResultText.Foreground = Brushes.ForestGreen;
                UpdateGoogleAccountUi();
                await _module.SyncCalendarAsync();
                UpdateCalendarStatus();
            }
            else
            {
                GoogleLoginResultText.Text = $"ログイン失敗: {msg}";
                GoogleLoginResultText.Foreground = Brushes.Tomato;
            }
        }

        GoogleLoginBtn.IsEnabled = true;
    }

    private void OnGoogleLogoutClicked(object sender, RoutedEventArgs e)
    {
        _module.CalendarService?.GoogleAuth.SignOut();
        UpdateGoogleAccountUi();
        GoogleLoginResultText.Text = "ログアウトしました。";
        GoogleLoginResultText.Foreground = Brushes.Gray;
        GoogleLoginResultText.Visibility = Visibility.Visible;
        _ = _module.SyncCalendarAsync();
        UpdateCalendarStatus();
    }

    private void UpdateGoogleAccountUi()
    {
        var s = _module.Settings;
        bool isSignedIn = _module.CalendarService?.GoogleAuth.IsSignedIn == true;
        if (isSignedIn)
        {
            GoogleAccountStatusText.Text = $"接続済み: {s.GoogleAccountEmail}";
            GoogleAccountStatusText.Foreground = Brushes.DodgerBlue;
            GoogleLoginBtn.Visibility = Visibility.Collapsed;
            GoogleLogoutBtn.Visibility = Visibility.Visible;
        }
        else
        {
            GoogleAccountStatusText.Text = "未連携";
            GoogleAccountStatusText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray;
            GoogleLoginBtn.Visibility = Visibility.Visible;
            GoogleLogoutBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void OnICloudSyncChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.ICloudSyncEnabled = ICloudSyncSwitch.IsChecked ?? false;
        Save();
        _ = _module.SyncCalendarAsync();
    }

    private void OnICloudCredentialsChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _module.Settings.ICloudAppleId = ICloudAppleIdBox.Text.Trim();
        _module.Settings.ICloudAppSpecificPasswordEncrypted = SecurityHelper.EncryptString(ICloudAppPwBox.Text.Trim());
        _module.Settings.ICloudCalendarName = !string.IsNullOrWhiteSpace(ICloudCalendarNameBox.Text.Trim())
            ? ICloudCalendarNameBox.Text.Trim()
            : "iCloud カレンダー";
        Save();
    }

    private void UpdateICloudAccountUi()
    {
        var s = _module.Settings;
        if (!string.IsNullOrWhiteSpace(s.ICloudAppleId) && !string.IsNullOrWhiteSpace(s.ICloudAppSpecificPasswordEncrypted))
        {
            ICloudAccountStatusText.Text = $"接続済み: {s.ICloudAppleId}";
            ICloudAccountStatusText.Foreground = Brushes.ForestGreen;
        }
        else
        {
            ICloudAccountStatusText.Text = "未連携";
            ICloudAccountStatusText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray;
        }
    }

    private async void OnICloudTestClicked(object sender, RoutedEventArgs e)
    {
        string appleId = ICloudAppleIdBox.Text.Trim();
        string appPw = ICloudAppPwBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(appleId) || string.IsNullOrWhiteSpace(appPw))
        {
            ICloudTestResultText.Text = "エラー: Apple ID と App用パスワードを入力してください。";
            ICloudTestResultText.Foreground = Brushes.Tomato;
            ICloudTestResultText.Visibility = Visibility.Visible;
            return;
        }

        ICloudTestBtn.IsEnabled = false;
        ICloudTestResultText.Text = "iCloud CalDAV サーバーに接続中...";
        ICloudTestResultText.Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush") ?? Brushes.DodgerBlue;
        ICloudTestResultText.Visibility = Visibility.Visible;

        using var client = new ICloudCalDavClient(appleId, appPw);
        var (success, msg, calendars) = await client.TestConnectionAsync();

        if (success)
        {
            string calSummary = calendars.Count > 0
                ? string.Join(", ", calendars.Select(c => c.Name))
                : "検出成功";

            if (calendars.Count > 0 && string.IsNullOrWhiteSpace(ICloudCalendarNameBox.Text.Trim()))
            {
                ICloudCalendarNameBox.Text = calendars[0].Name;
                _module.Settings.ICloudCalendarUrl = calendars[0].Url;
                _module.Settings.ICloudCalendarName = calendars[0].Name;
                Save();
            }

            ICloudTestResultText.Text = $"接続成功: {calendars.Count}件のカレンダーを検出 ({calSummary})";
            ICloudTestResultText.Foreground = Brushes.ForestGreen;
            UpdateICloudAccountUi();

            await _module.SyncCalendarAsync();
            UpdateCalendarStatus();
        }
        else
        {
            ICloudTestResultText.Text = $"接続失敗: {msg}";
            ICloudTestResultText.Foreground = Brushes.Tomato;
        }

        ICloudTestBtn.IsEnabled = true;
    }

    private void OnOpenAppleIdSecurityClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://appleid.apple.com/account/manage");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
