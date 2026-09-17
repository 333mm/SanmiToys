using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SanmiToys.Core.Services;
using SanmiToys.Modules.FluidDrag.Core;
using SanmiToys.Modules.FluidDrag.Models;
using Wpf.Ui.Controls;

namespace SanmiToys.Modules.FluidDrag.Views;

public partial class FluidDragSettingsView : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly FluidDragModule _module;
    private readonly SettingsService _settingsService;
    private readonly FluidDragSettings _settings;
    private bool _isInitializing = true;

    private ObservableCollection<string> _excludedProcesses = new();
    private ObservableCollection<string> _excludedTitles = new();
    private ObservableCollection<string> _whitelistedProcesses = new();
    private ObservableCollection<string> _whitelistedTitles = new();
    private ObservableCollection<RunningAppInfo> _runningApps = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AddAppButtonText => _settings.FilterMode == FluidDragFilterMode.Whitelist
        ? LocalizationService.Instance["FluidDrag_RunningApps_Add_Whitelist"]
        : LocalizationService.Instance["FluidDrag_RunningApps_Add_Blacklist"];

    public FluidDragSettingsView(FluidDragModule module, SettingsService settingsService, FluidDragSettings settings)
    {
        InitializeComponent();
        _module = module;
        _settingsService = settingsService;
        _settings = settings;

        DataContext = this;

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

        FilterModeCombo.SelectedIndex = (int)_settings.FilterMode;

        _excludedProcesses = new ObservableCollection<string>(_settings.ExcludedProcesses);
        _excludedTitles = new ObservableCollection<string>(_settings.ExcludedWindowTitles);
        _whitelistedProcesses = new ObservableCollection<string>(_settings.WhitelistedProcesses);
        _whitelistedTitles = new ObservableCollection<string>(_settings.WhitelistedWindowTitles);

        UpdateFilterModeUI();
        RefreshRunningApps();
    }

    private void UpdateFilterModeUI()
    {
        bool isWhite = _settings.FilterMode == FluidDragFilterMode.Whitelist;

        // セクションヘッダー
        FilterSectionHeaderText.Text = isWhite
            ? LocalizationService.Instance["FluidDrag_FilterSection_Whitelist"]
            : LocalizationService.Instance["FluidDrag_ExclusionsSection"];
        FilterSectionHeaderIcon.Symbol = isWhite ? SymbolRegular.CheckmarkSquare24 : SymbolRegular.Filter24;

        var cautionBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("SystemFillColorCautionBrush");
        var successBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("SystemFillColorSuccessBrush");
        var activeBrush = isWhite ? successBrush : cautionBrush;

        // プロセスカード
        ProcessSectionIcon.Symbol = isWhite ? SymbolRegular.CheckmarkSquare24 : SymbolRegular.DismissSquare24;
        ProcessSectionIcon.Foreground = activeBrush;
        ProcessSectionTitle.Text = isWhite
            ? LocalizationService.Instance["FluidDrag_Process_Title_Whitelist"]
            : LocalizationService.Instance["FluidDrag_Process_Title_Blacklist"];
        ProcessSectionDesc.Text = isWhite
            ? LocalizationService.Instance["FluidDrag_Process_Desc_Whitelist"]
            : LocalizationService.Instance["FluidDrag_Process_Desc_Blacklist"];
        ProcessEmptyText.Text = isWhite
            ? LocalizationService.Instance["FluidDrag_Process_Empty_Whitelist"]
            : LocalizationService.Instance["FluidDrag_Process_Empty_Blacklist"];

        var currentProcesses = isWhite ? _whitelistedProcesses : _excludedProcesses;
        ProcessesPanel.ItemsSource = currentProcesses;
        ProcessCountText.Text = currentProcesses.Count.ToString();
        ProcessEmptyText.Visibility = currentProcesses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // タイトルカード
        TitleSectionIcon.Symbol = isWhite ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24;
        TitleSectionIcon.Foreground = activeBrush;
        TitleSectionTitle.Text = isWhite
            ? LocalizationService.Instance["FluidDrag_Title_Title_Whitelist"]
            : LocalizationService.Instance["FluidDrag_Title_Title_Blacklist"];
        TitleSectionDesc.Text = isWhite
            ? LocalizationService.Instance["FluidDrag_Title_Desc_Whitelist"]
            : LocalizationService.Instance["FluidDrag_Title_Desc_Blacklist"];
        TitleEmptyText.Text = isWhite
            ? LocalizationService.Instance["FluidDrag_Title_Empty_Whitelist"]
            : LocalizationService.Instance["FluidDrag_Title_Empty_Blacklist"];

        var currentTitles = isWhite ? _whitelistedTitles : _excludedTitles;
        TitlesPanel.ItemsSource = currentTitles;
        TitleCountText.Text = currentTitles.Count.ToString();
        TitleEmptyText.Visibility = currentTitles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 起動中アプリのボタンテキスト更新通知
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddAppButtonText)));
    }

    private void RefreshRunningApps()
    {
        _ = RefreshRunningAppsAsync();
    }

    private async System.Threading.Tasks.Task RefreshRunningAppsAsync()
    {
        var apps = await System.Threading.Tasks.Task.Run(() => RunningAppFinder.GetRunningWindows());
        _runningApps = new ObservableCollection<RunningAppInfo>(apps);
        RunningAppsList.ItemsSource = _runningApps;
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;
        _settings.FilterMode = (FluidDragFilterMode)FilterModeCombo.SelectedIndex;
        _settings.ExcludedProcesses = _excludedProcesses.ToList();
        _settings.ExcludedWindowTitles = _excludedTitles.ToList();
        _settings.WhitelistedProcesses = _whitelistedProcesses.ToList();
        _settings.WhitelistedWindowTitles = _whitelistedTitles.ToList();
        _settingsService.SetModuleSettings(_module.Id, _settings);
    }

    private void OnFilterModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.FilterMode = (FluidDragFilterMode)FilterModeCombo.SelectedIndex;
        UpdateFilterModeUI();
        SaveSettings();
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

        var list = _settings.FilterMode == FluidDragFilterMode.Whitelist ? _whitelistedProcesses : _excludedProcesses;
        if (!list.Any(p => string.Equals(p, clean, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(clean);
            ProcessCountText.Text = list.Count.ToString();
            ProcessEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveSettings();
        }
    }

    private void OnRemoveProcessClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string procName)
        {
            var list = _settings.FilterMode == FluidDragFilterMode.Whitelist ? _whitelistedProcesses : _excludedProcesses;
            list.Remove(procName);
            ProcessCountText.Text = list.Count.ToString();
            ProcessEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        var list = _settings.FilterMode == FluidDragFilterMode.Whitelist ? _whitelistedTitles : _excludedTitles;
        if (!list.Any(t => string.Equals(t, clean, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(clean);
            TitleCountText.Text = list.Count.ToString();
            TitleEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveSettings();
        }
    }

    private void OnRemoveTitleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string title)
        {
            var list = _settings.FilterMode == FluidDragFilterMode.Whitelist ? _whitelistedTitles : _excludedTitles;
            list.Remove(title);
            TitleCountText.Text = list.Count.ToString();
            TitleEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveSettings();
        }
    }
}
