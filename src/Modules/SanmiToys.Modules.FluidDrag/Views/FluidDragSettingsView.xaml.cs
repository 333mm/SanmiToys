using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SanmiToys.Core.Services;
using SanmiToys.Modules.FluidDrag.Core;
using SanmiToys.Modules.FluidDrag.Models;

namespace SanmiToys.Modules.FluidDrag.Views;

public partial class FluidDragSettingsView : System.Windows.Controls.UserControl
{
    private readonly FluidDragModule _module;
    private readonly SettingsService _settingsService;
    private readonly FluidDragSettings _settings;
    private bool _isInitializing = true;

    private ObservableCollection<string> _excludedProcesses = new();
    private ObservableCollection<string> _excludedTitles = new();
    private ObservableCollection<RunningAppInfo> _runningApps = new();

    public FluidDragSettingsView(FluidDragModule module, SettingsService settingsService, FluidDragSettings settings)
    {
        InitializeComponent();
        _module = module;
        _settingsService = settingsService;
        _settings = settings;

        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        EnableSwitch.IsChecked = _settings.IsEnabled;
        EnableModifierCombo.SelectedIndex = (int)_settings.EnableModifierKey;
        DisableModifierCombo.SelectedIndex = (int)_settings.DisableModifierKey;
        ThresholdSlider.Value = _settings.DragThresholdPixels;
        ThresholdText.Text = $"{_settings.DragThresholdPixels} px";

        DisableFullscreenSwitch.IsChecked = _settings.DisableWhenFullscreen;
        ExcludeMaximizedSwitch.IsChecked = _settings.ExcludeMaximizedWindows;

        _excludedProcesses = new ObservableCollection<string>(_settings.ExcludedProcesses);
        ExcludedProcessesPanel.ItemsSource = _excludedProcesses;

        _excludedTitles = new ObservableCollection<string>(_settings.ExcludedWindowTitles);
        ExcludedTitlesPanel.ItemsSource = _excludedTitles;

        RefreshRunningApps();
    }

    private void RefreshRunningApps()
    {
        var apps = RunningAppFinder.GetRunningWindows();
        _runningApps = new ObservableCollection<RunningAppInfo>(apps);
        RunningAppsList.ItemsSource = _runningApps;
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;
        _settings.ExcludedProcesses = _excludedProcesses.ToList();
        _settings.ExcludedWindowTitles = _excludedTitles.ToList();
        _settingsService.SetModuleSettings(_module.Id, _settings);
    }

    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.IsEnabled = EnableSwitch.IsChecked == true;
    }

    private void OnModifierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.EnableModifierKey = (ModifierKeyMode)EnableModifierCombo.SelectedIndex;
        _settings.DisableModifierKey = (ModifierKeyMode)DisableModifierCombo.SelectedIndex;
        SaveSettings();
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int val = (int)ThresholdSlider.Value;
        _settings.DragThresholdPixels = val;
        ThresholdText.Text = $"{val} px";
        SaveSettings();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.DisableWhenFullscreen = DisableFullscreenSwitch.IsChecked == true;
        _settings.ExcludeMaximizedWindows = ExcludeMaximizedSwitch.IsChecked == true;
        SaveSettings();
    }

    private void OnRefreshRunningAppsClicked(object sender, RoutedEventArgs e)
    {
        RefreshRunningApps();
    }

    private void OnAddRunningAppClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string procName && !string.IsNullOrWhiteSpace(procName))
        {
            AddProcess(procName);
        }
    }

    private void OnAddProcessClicked(object sender, RoutedEventArgs e)
    {
        AddProcess(NewProcessBox.Text);
        NewProcessBox.Text = string.Empty;
    }

    private void OnNewProcessKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddProcess(NewProcessBox.Text);
            NewProcessBox.Text = string.Empty;
        }
    }

    private void AddProcess(string procName)
    {
        if (string.IsNullOrWhiteSpace(procName)) return;
        string clean = procName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        if (!_excludedProcesses.Any(p => string.Equals(p, clean, StringComparison.OrdinalIgnoreCase)))
        {
            _excludedProcesses.Add(clean);
            SaveSettings();
        }
    }

    private void OnRemoveProcessClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string procName)
        {
            _excludedProcesses.Remove(procName);
            SaveSettings();
        }
    }

    private void OnAddTitleClicked(object sender, RoutedEventArgs e)
    {
        AddTitle(NewTitleBox.Text);
        NewTitleBox.Text = string.Empty;
    }

    private void OnNewTitleKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTitle(NewTitleBox.Text);
            NewTitleBox.Text = string.Empty;
        }
    }

    private void AddTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        string clean = title.Trim();
        if (!_excludedTitles.Any(t => string.Equals(t, clean, StringComparison.OrdinalIgnoreCase)))
        {
            _excludedTitles.Add(clean);
            SaveSettings();
        }
    }

    private void OnRemoveTitleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string title)
        {
            _excludedTitles.Remove(title);
            SaveSettings();
        }
    }
}
