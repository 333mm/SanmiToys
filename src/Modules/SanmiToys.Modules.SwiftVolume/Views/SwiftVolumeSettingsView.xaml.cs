using System.Windows;
using System.Windows.Controls;
using SanmiToys.Core.Services;
using SanmiToys.Modules.SwiftVolume.Core;
using SanmiToys.Modules.SwiftVolume.Models;

namespace SanmiToys.Modules.SwiftVolume.Views;

public partial class SwiftVolumeSettingsView : System.Windows.Controls.UserControl
{
    private readonly SwiftVolumeModule _module;
    private readonly SettingsService _settingsService;
    private readonly SwiftVolumeSettings _settings;
    private bool _isInitializing = true;

    public SwiftVolumeSettingsView(SwiftVolumeModule module, SettingsService settingsService, SwiftVolumeSettings settings)
    {
        InitializeComponent();
        _module = module;
        _settingsService = settingsService;
        _settings = settings;

        LoadSettingsToUi();
        _isInitializing = false;
    }

    private void LoadSettingsToUi()
    {
        EnableSwitch.IsChecked = _settings.IsEnabled;
        OpenAtCursorSwitch.IsChecked = _settings.OpenAtCursor;
        MiddleClickMuteSwitch.IsChecked = _settings.MiddleClickMuteAll;
        EnableTaskbarWheelSwitch.IsChecked = _settings.EnableTaskbarVolumeWheel;
        TaskbarWheelTrayOnlySwitch.IsChecked = _settings.TaskbarWheelTrayIconOnly;
        TaskbarWheelOptionsPanel.Visibility = _settings.EnableTaskbarVolumeWheel ? Visibility.Visible : Visibility.Collapsed;
        DefaultAppVolumeSlider.Value = _settings.DefaultAppVolumePercent;
        DefaultAppVolumeText.Text = $"{_settings.DefaultAppVolumePercent}%";
        ShowHudSwitch.IsChecked = _settings.ShowHud;
        ShowDeviceSwitchHudSwitch.IsChecked = _settings.ShowDeviceSwitchHud;
        HudPositionCombo.SelectedIndex = Math.Clamp(_settings.HudPosition, 0, 6);
        HudSizeCombo.SelectedIndex = Math.Clamp(_settings.HudSize, 0, 2);

        ShowMicTrayIconSwitch.IsChecked = _settings.ShowMicTrayIcon;
        EnableMicGlowSwitch.IsChecked = _settings.EnableMicGlow;
        MicGlowOptionsPanel.Visibility = _settings.ShowMicTrayIcon ? Visibility.Visible : Visibility.Collapsed;

        EnableMicMonitoringSwitch.IsChecked = _settings.EnableMicMonitoring;
        MicMonitoringOptionsPanel.Visibility = _settings.EnableMicMonitoring ? Visibility.Visible : Visibility.Collapsed;
        MicMonitoringVolumeSlider.Value = _settings.MicMonitoringVolumePercent;
        MicMonitoringVolumeText.Text = $"{_settings.MicMonitoringVolumePercent}%";
        PopulateOutputDevices();

        DeviceHudOptionsPanel.Visibility = _settings.ShowDeviceSwitchHud ? Visibility.Visible : Visibility.Collapsed;

        UpdateAllHotkeyDisplays();
    }

    private void UpdateAllHotkeyDisplays()
    {
        UpdateHotkeyDisplay(HotkeyOpenMixerBox, _settings.HotkeyOpenMixerEnabled, _settings.HotkeyOpenMixerCtrl, _settings.HotkeyOpenMixerAlt, _settings.HotkeyOpenMixerShift, _settings.HotkeyOpenMixerWin, _settings.HotkeyOpenMixer);
        UpdateHotkeyDisplay(HotkeyMuteBox, _settings.HotkeyMuteEnabled, _settings.HotkeyMuteCtrl, _settings.HotkeyMuteAlt, _settings.HotkeyMuteShift, _settings.HotkeyMuteWin, _settings.HotkeyMute);
        UpdateHotkeyDisplay(HotkeyMicMuteBox, _settings.HotkeyMicMuteEnabled, _settings.HotkeyMicMuteCtrl, _settings.HotkeyMicMuteAlt, _settings.HotkeyMicMuteShift, _settings.HotkeyMicMuteWin, _settings.HotkeyMicMute);
    }

    private static void UpdateHotkeyDisplay(Wpf.Ui.Controls.TextBox box, bool enabled, bool ctrl, bool alt, bool shift, bool win, string key)
    {
        if (!enabled || string.IsNullOrEmpty(key) || key == "None")
        {
            box.Text = SanmiToys.Core.Services.LocalizationService.Instance["Common_NotSet"];
            return;
        }

        var parts = new System.Collections.Generic.List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key);

        box.Text = string.Join(" + ", parts);
    }

    private void OnHotkeyGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.TextBox box)
        {
            box.Text = SanmiToys.Core.Services.LocalizationService.Instance["Common_PressKeys"];
        }
    }

    private void OnHotkeyLostFocus(object sender, RoutedEventArgs e)
    {
        UpdateAllHotkeyDisplays();
    }

    private void OnHotkeyPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.TextBox box || box.Tag is not string tag) return;

        e.Handled = true;

        if (e.Key == System.Windows.Input.Key.Escape)
        {
            System.Windows.Input.Keyboard.ClearFocus();
            return;
        }

        if (e.Key is System.Windows.Input.Key.Delete or System.Windows.Input.Key.Back)
        {
            ClearHotkeyByTag(tag);
            System.Windows.Input.Keyboard.ClearFocus();
            return;
        }

        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool alt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0 || e.Key == System.Windows.Input.Key.System;
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        bool win = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Windows) != 0 || 
                   System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LWin) || 
                   System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RWin);

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or
                   System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or
                   System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or
                   System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin or
                   System.Windows.Input.Key.None)
        {
            var tempParts = new System.Collections.Generic.List<string>();
            if (ctrl) tempParts.Add("Ctrl");
            if (alt) tempParts.Add("Alt");
            if (shift) tempParts.Add("Shift");
            if (win) tempParts.Add("Win");
            tempParts.Add("...");
            box.Text = string.Join(" + ", tempParts);
            return;
        }

        string keyName = key.ToString();
        if (keyName.StartsWith("D") && keyName.Length == 2 && char.IsDigit(keyName[1]))
        {
            keyName = keyName.Substring(1);
        }
        else if (keyName.StartsWith("NumPad") && keyName.Length == 7 && char.IsDigit(keyName[6]))
        {
            keyName = "Num" + keyName.Substring(6);
        }

        SetHotkeyByTag(tag, true, ctrl, alt, shift, win, keyName);
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private void SetHotkeyByTag(string tag, bool enabled, bool ctrl, bool alt, bool shift, bool win, string key)
    {
        switch (tag)
        {
            case "OpenMixer":
                _settings.HotkeyOpenMixerEnabled = enabled;
                _settings.HotkeyOpenMixerCtrl = ctrl;
                _settings.HotkeyOpenMixerAlt = alt;
                _settings.HotkeyOpenMixerShift = shift;
                _settings.HotkeyOpenMixerWin = win;
                _settings.HotkeyOpenMixer = key;
                break;
            case "Mute":
                _settings.HotkeyMuteEnabled = enabled;
                _settings.HotkeyMuteCtrl = ctrl;
                _settings.HotkeyMuteAlt = alt;
                _settings.HotkeyMuteShift = shift;
                _settings.HotkeyMuteWin = win;
                _settings.HotkeyMute = key;
                break;
            case "MicMute":
                _settings.HotkeyMicMuteEnabled = enabled;
                _settings.HotkeyMicMuteCtrl = ctrl;
                _settings.HotkeyMicMuteAlt = alt;
                _settings.HotkeyMicMuteShift = shift;
                _settings.HotkeyMicMuteWin = win;
                _settings.HotkeyMicMute = key;
                break;
        }

        SaveSettings();
        _module.UpdateHotkeyRegistrations();
        UpdateAllHotkeyDisplays();
    }

    private void ClearHotkeyByTag(string tag)
    {
        SetHotkeyByTag(tag, false, false, false, false, false, "None");
    }

    private void OnClearHotkeyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement elem && elem.Tag is string tag)
        {
            ClearHotkeyByTag(tag);
        }
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;
        _settingsService.SetModuleSettings(_module.Id, _settings);
        _module.NotifySettingsChanged();
    }

    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.IsEnabled = EnableSwitch.IsChecked == true;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.ShowMicTrayIcon = ShowMicTrayIconSwitch.IsChecked == true;
        _settings.EnableMicGlow = EnableMicGlowSwitch.IsChecked == true;
        _settings.OpenAtCursor = OpenAtCursorSwitch.IsChecked == true;
        _settings.MiddleClickMuteAll = MiddleClickMuteSwitch.IsChecked == true;
        _settings.EnableTaskbarVolumeWheel = EnableTaskbarWheelSwitch.IsChecked == true;
        _settings.TaskbarWheelTrayIconOnly = TaskbarWheelTrayOnlySwitch.IsChecked == true;
        _settings.ShowHud = ShowHudSwitch.IsChecked == true;
        _settings.ShowDeviceSwitchHud = ShowDeviceSwitchHudSwitch.IsChecked == true;

        bool prevMonitoring = _settings.EnableMicMonitoring;
        _settings.EnableMicMonitoring = EnableMicMonitoringSwitch.IsChecked == true;
        MicMonitoringOptionsPanel.Visibility = _settings.EnableMicMonitoring ? Visibility.Visible : Visibility.Collapsed;

        if (prevMonitoring != _settings.EnableMicMonitoring)
        {
            if (_settings.EnableMicMonitoring)
            {
                _module.MonitorEngine?.Start();
            }
            else
            {
                _module.MonitorEngine?.Stop();
            }
        }

        MicGlowOptionsPanel.Visibility = _settings.ShowMicTrayIcon ? Visibility.Visible : Visibility.Collapsed;
        TaskbarWheelOptionsPanel.Visibility = _settings.EnableTaskbarVolumeWheel ? Visibility.Visible : Visibility.Collapsed;
        DeviceHudOptionsPanel.Visibility = _settings.ShowDeviceSwitchHud ? Visibility.Visible : Visibility.Collapsed;
        SaveSettings();
    }

    private void PopulateOutputDevices()
    {
        MicMonitoringOutputCombo.Items.Clear();
        string defaultLabel = SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_DefaultOutputDevice"];
        var defaultItem = new ComboBoxItem { Content = defaultLabel, Tag = "" };
        MicMonitoringOutputCombo.Items.Add(defaultItem);

        int selectedIndex = 0;
        try
        {
            using var devService = new DeviceEnumerationService();
            var devices = devService.GetSafeOutputDevices();
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                var item = new ComboBoxItem { Content = d.Name, Tag = d.Id };
                MicMonitoringOutputCombo.Items.Add(item);
                if (!string.IsNullOrEmpty(_settings.MicMonitoringOutputDeviceId) && d.Id == _settings.MicMonitoringOutputDeviceId)
                {
                    selectedIndex = i + 1;
                }
            }
        }
        catch { }

        MicMonitoringOutputCombo.SelectedIndex = selectedIndex;
    }

    private void OnMicMonitoringVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)Math.Round(MicMonitoringVolumeSlider.Value);
        _settings.MicMonitoringVolumePercent = val;
        if (MicMonitoringVolumeText != null)
        {
            MicMonitoringVolumeText.Text = $"{val}%";
        }
        _module.MonitorEngine?.UpdateVolume(val / 100f);
        SaveSettings();
    }

    private void OnMicMonitoringOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (MicMonitoringOutputCombo.SelectedItem is ComboBoxItem item)
        {
            string devId = item.Tag as string ?? "";
            _settings.MicMonitoringOutputDeviceId = devId;
            SaveSettings();
            if (_settings.EnableMicMonitoring)
            {
                _module.MonitorEngine?.Restart();
            }
        }
    }

    private void OnDefaultAppVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)Math.Round(DefaultAppVolumeSlider.Value);
        _settings.DefaultAppVolumePercent = val;
        if (DefaultAppVolumeText != null)
        {
            DefaultAppVolumeText.Text = $"{val}%";
        }
        SaveSettings();
    }

    private void OnHudPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.HudPosition = HudPositionCombo.SelectedIndex;
        SaveSettings();
    }

    private void OnHudSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.HudSize = HudSizeCombo.SelectedIndex;
        SaveSettings();
    }

    private void OnOpenMixerClicked(object sender, RoutedEventArgs e)
    {
        _module.TriggerOpenMixer();
    }
}
