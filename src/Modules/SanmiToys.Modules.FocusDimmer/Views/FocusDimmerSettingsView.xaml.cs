using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SanmiToys.Core.Services;
using SanmiToys.Modules.FocusDimmer.Models;

namespace SanmiToys.Modules.FocusDimmer.Views;

public partial class FocusDimmerSettingsView : System.Windows.Controls.UserControl
{
    private readonly FocusDimmerModule _module;
    private readonly SettingsService _settingsService;
    private readonly FocusDimmerSettings _settings;
    private MonitorProfile _activeProfile;
    private bool _isInitializing = true;

    public FocusDimmerSettingsView(FocusDimmerModule module, SettingsService settingsService, FocusDimmerSettings settings)
    {
        InitializeComponent();
        _module = module;
        _settingsService = settingsService;
        _settings = settings;

        _activeProfile = _settings.Profiles.FirstOrDefault() ?? _settings.DefaultProfile;

        InitMonitorCombo();
        LoadSettingsToUi();
        _isInitializing = false;
    }

    private void InitMonitorCombo()
    {
        MonitorCombo.Items.Clear();
        foreach (var prof in _settings.Profiles)
        {
            MonitorCombo.Items.Add(prof.FriendlyName);
        }
        if (_settings.Profiles.Count > 0)
        {
            MonitorCombo.SelectedIndex = 0;
            _activeProfile = _settings.Profiles[0];
        }
    }

    private void LoadSettingsToUi()
    {
        EnableSwitch.IsChecked = _settings.IsEnabled;
        AreMonitorsLinkedSwitch.IsChecked = _settings.AreMonitorsLinked;
        MonitorSelectorCard.Visibility = _settings.AreMonitorsLinked ? Visibility.Collapsed : Visibility.Visible;

        var profile = _settings.AreMonitorsLinked ? _settings.DefaultProfile : _activeProfile;

        OpacitySlider.Value = profile.Opacity;
        OpacityText.Text = $"{(int)profile.Opacity}%";
        ColorBox.Text = profile.OverlayColorHex;

        DimDesktopOnlySwitch.IsChecked = profile.DimDesktopOnly;
        ExcludeTaskbarSwitch.IsChecked = profile.ExcludeTaskbar;
        ExcludeTopmostSwitch.IsChecked = profile.ExcludeTopmost;

        DimWhenIdleSwitch.IsChecked = profile.DimWhenIdle;
        IdleDimOptionsPanel.Visibility = profile.DimWhenIdle ? Visibility.Visible : Visibility.Collapsed;
        IdleTimeoutSlider.Value = profile.IdleTimeout;
        IdleTimeoutText.Text = $"{profile.IdleTimeout}分";
        IdleOpacitySlider.Value = profile.IdleDimOpacity;
        IdleOpacityText.Text = $"{(int)profile.IdleDimOpacity}%";

        AlwaysBrightBox.Text = _settings.AlwaysBrightList;
        AlwaysDarkBox.Text = _settings.AlwaysDarkList;
    }

    private void SyncSettingsToTargets(System.Action<MonitorProfile> updateAction)
    {
        if (_isInitializing) return;

        if (_settings.AreMonitorsLinked)
        {
            updateAction(_settings.DefaultProfile);
            foreach (var p in _settings.Profiles)
            {
                updateAction(p);
            }
        }
        else
        {
            updateAction(_activeProfile);
        }

        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;
        _settingsService.SetModuleSettings(_module.Id, _settings);
    }

    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.IsEnabled = EnableSwitch.IsChecked == true;
    }

    private void OnMonitorsLinkedChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.AreMonitorsLinked = AreMonitorsLinkedSwitch.IsChecked == true;
        MonitorSelectorCard.Visibility = _settings.AreMonitorsLinked ? Visibility.Collapsed : Visibility.Visible;

        if (_settings.AreMonitorsLinked)
        {
            // 全モニターに DefaultProfile を同期
            foreach (var p in _settings.Profiles)
            {
                p.CopyFrom(_settings.DefaultProfile);
            }
        }
        SaveSettings();
        LoadSettingsToUi();
    }

    private void OnMonitorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        int idx = MonitorCombo.SelectedIndex;
        if (idx >= 0 && idx < _settings.Profiles.Count)
        {
            _activeProfile = _settings.Profiles[idx];
            _isInitializing = true;
            LoadSettingsToUi();
            _isInitializing = false;
        }
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        double val = OpacitySlider.Value;
        OpacityText.Text = $"{(int)val}%";
        SyncSettingsToTargets(p => p.Opacity = val);
    }

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ColorBox.Text))
        {
            string hex = ColorBox.Text.Trim();
            try
            {
                var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
                ColorPreviewBorder.Background = brush;
            }
            catch { }

            if (!_isInitializing)
            {
                SyncSettingsToTargets(p => p.OverlayColorHex = hex);
            }
        }
    }

    private void OnPickColorClicked(object sender, RoutedEventArgs e)
    {
        string currentHex = !string.IsNullOrWhiteSpace(ColorBox.Text) ? ColorBox.Text.Trim() : "#000000";
        var picker = new ModernColorPickerWindow(currentHex)
        {
            Owner = Window.GetWindow(this)
        };

        if (picker.ShowDialog() == true)
        {
            ColorBox.Text = picker.SelectedColorHex;
        }
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool dimDesktop = DimDesktopOnlySwitch.IsChecked == true;
        bool exTaskbar = ExcludeTaskbarSwitch.IsChecked == true;
        bool exTopmost = ExcludeTopmostSwitch.IsChecked == true;
        bool dimIdle = DimWhenIdleSwitch.IsChecked == true;

        IdleDimOptionsPanel.Visibility = dimIdle ? Visibility.Visible : Visibility.Collapsed;

        SyncSettingsToTargets(p =>
        {
            p.DimDesktopOnly = dimDesktop;
            p.ExcludeTaskbar = exTaskbar;
            p.ExcludeTopmost = exTopmost;
            p.DimWhenIdle = dimIdle;
        });
    }

    private void OnIdleTimeoutChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)IdleTimeoutSlider.Value;
        IdleTimeoutText.Text = LocalizationService.Instance.EffectiveLanguageCode == "ja" ? $"{val}分" : $"{val} min";
        SyncSettingsToTargets(p => p.IdleTimeout = val);
    }

    private void OnIdleOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        double val = IdleOpacitySlider.Value;
        IdleOpacityText.Text = $"{(int)val}%";
        SyncSettingsToTargets(p => p.IdleDimOpacity = val);
    }

    private void OnListChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.AlwaysBrightList = AlwaysBrightBox.Text.Trim();
        _settings.AlwaysDarkList = AlwaysDarkBox.Text.Trim();
        SaveSettings();
    }

    private Helpers.DebugInspector? _inspector;

    private void OnLaunchInspectorClicked(object sender, RoutedEventArgs e)
    {
        _inspector?.Stop();
        _inspector = new Helpers.DebugInspector(_settings);

        // インスペクター起動時の画面の減光状態をそのままフリーズ・維持
        _module.SetInspectorMode(true);

        _inspector.StopRequested += (s, e) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _inspector?.Stop();
                _module.SetInspectorMode(false);
            });
        };

        _inspector.SelectedWindowCaptured += (s, data) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _inspector?.Stop();
                var dialog = new InspectorActionDialog(data.ProcessName, data.Title);
                if (dialog.ShowDialog() == true)
                {
                    string proc = data.ProcessName.Trim();
                    if (!string.IsNullOrEmpty(proc))
                    {
                        if (dialog.ActionType == "Bright")
                        {
                            _settings.AlwaysBrightList = AddProcessToCsv(_settings.AlwaysBrightList, proc);
                            AlwaysBrightBox.Text = _settings.AlwaysBrightList;
                        }
                        else if (dialog.ActionType == "Dark")
                        {
                            _settings.AlwaysDarkList = AddProcessToCsv(_settings.AlwaysDarkList, proc);
                            AlwaysDarkBox.Text = _settings.AlwaysDarkList;
                        }
                        else if (dialog.ActionType == "Copy")
                        {
                            try { System.Windows.Clipboard.SetText(proc); } catch { }
                        }
                        SaveSettings();
                    }
                }
                _module.SetInspectorMode(false);
            });
        };

        _inspector.Start();
    }

    private static string AddProcessToCsv(string csv, string procName)
    {
        var list = (csv ?? "").Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
        string lower = procName.ToLower();
        if (!list.Any(x => x.Equals(lower, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(procName);
        }
        return string.Join(", ", list);
    }
}
