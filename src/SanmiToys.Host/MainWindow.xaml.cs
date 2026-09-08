using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using SanmiToys.Host.Services;
using SanmiToys.Host.Views;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace SanmiToys.Host;

public class SanmiToysPageProvider : INavigationViewPageProvider
{
    private readonly List<IToyModule> _modules;
    private readonly Action<string> _navigateAction;
    private readonly Dictionary<Type, object> _pageCache = new();

    public SanmiToysPageProvider(List<IToyModule> modules, Action<string> navigateAction)
    {
        _modules = modules;
        _navigateAction = navigateAction;
    }

    public object? GetPage(Type pageType)
    {
        if (_pageCache.TryGetValue(pageType, out var cached))
        {
            return cached;
        }

        object? newPage = null;
        if (pageType == typeof(DashboardPage))
        {
            newPage = new DashboardPage(_modules, _navigateAction);
        }
        else if (pageType == typeof(GeneralSettingsPage))
        {
            newPage = new GeneralSettingsPage();
        }
        else if (pageType == typeof(FluidDragPage))
        {
            var mod = _modules.Find(m => m.Id == "FluidDrag");
            newPage = mod != null ? new FluidDragPage(mod) : null;
        }
        else if (pageType == typeof(FocusDimmerPage))
        {
            var mod = _modules.Find(m => m.Id == "FocusDimmer");
            newPage = mod != null ? new FocusDimmerPage(mod) : null;
        }
        else if (pageType == typeof(SnapTransPage))
        {
            var mod = _modules.Find(m => m.Id == "SnapTrans");
            newPage = mod != null ? new SnapTransPage(mod) : null;
        }
        else if (pageType == typeof(SwiftVolumePage))
        {
            var mod = _modules.Find(m => m.Id == "SwiftVolume");
            newPage = mod != null ? new SwiftVolumePage(mod) : null;
        }
        else if (pageType == typeof(OmniGlancePage))
        {
            var mod = _modules.Find(m => m.Id == "OmniGlance");
            newPage = mod != null ? new OmniGlancePage(mod) : null;
        }

        if (newPage != null)
        {
            _pageCache[pageType] = newPage;
        }
        return newPage;
    }
}

public partial class MainWindow : FluentWindow
{
    private readonly List<IToyModule> _modules;
    private readonly SanmiToysPageProvider _pageProvider;
    private readonly TrayIconService _trayService;
    private bool _isRealExit = false;

    private bool _hasUpdateAvailable = false;
    private string _latestDetectedVersion = string.Empty;
    private string _updateReleaseUrl = string.Empty;
    private bool _isCheckingOrUpdating = false;

    public MainWindow(List<IToyModule> modules)
    {
        InitializeComponent();
        _modules = modules;

        _pageProvider = new SanmiToysPageProvider(_modules, NavigateToModule);
        RootNav.SetPageProviderService(_pageProvider);

        _trayService = new TrayIconService(_modules, ShowWindow, ExitApplication);

        ResetUpdateBtnToDefault();

        UpdateService.Instance.StartPeriodicUpdateCheck(result =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _hasUpdateAvailable = true;
                _latestDetectedVersion = result.LatestVersion;
                _updateReleaseUrl = result.ReleaseUrl;

                var loc = SanmiToys.Core.Services.LocalizationService.Instance;
                string title = loc["Nav_UpdateNotificationTitle"];
                string msg = string.Format(loc["Nav_UpdateNotificationBody"], result.LatestVersion);

                // Windows 通知オプション判定 (設定で有効な場合のみトースト通知)
                bool notifyEnabled = SanmiToys.Core.Services.SettingsService.Instance.GetGeneralSetting("NotifyOnUpdate", true);
                if (notifyEnabled)
                {
                    _trayService.ShowBalloonTip(title, msg);
                }

                ApplyUpdateAvailableUI();

                if (this.IsVisible && this.WindowState != WindowState.Minimized)
                {
                    try
                    {
                        RootNav.Navigate(typeof(DashboardPage));
                    }
                    catch { }
                }
            });
        }, TimeSpan.FromHours(1));

        SanmiToys.Core.Services.LocalizationService.Instance.LanguageChanged += () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isCheckingOrUpdating) return;
                if (_hasUpdateAvailable)
                {
                    ApplyUpdateAvailableUI();
                }
                else
                {
                    ResetUpdateBtnToDefault();
                }
            });
        };

        this.Loaded += (s, e) =>
        {
            try
            {
                if (RootNav.SelectedItem == null)
                {
                    RootNav.Navigate(typeof(DashboardPage));
                }
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("Host", $"Initial navigation warning: {ex.Message}");
            }
        };

        this.Closing += OnWindowClosing;
    }

    private void ApplyUpdateAvailableUI()
    {
        var loc = SanmiToys.Core.Services.LocalizationService.Instance;
        DirectUpdateBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        UpdateBtnIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
        UpdateBtnIcon.Visibility = Visibility.Visible;
        UpdateBtnRing.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(_latestDetectedVersion))
        {
            UpdateBtnText.Text = string.Format(loc["Nav_UpdateVersion"], _latestDetectedVersion);
            DirectUpdateBtn.ToolTip = string.Format(loc["General_UpdateAvailable"], _latestDetectedVersion);
        }
        else
        {
            UpdateBtnText.Text = loc["Nav_UpdateNow"];
            DirectUpdateBtn.ToolTip = loc["Nav_UpdateNotificationTitle"];
        }
    }

    private void ResetUpdateBtnToDefault()
    {
        var loc = SanmiToys.Core.Services.LocalizationService.Instance;
        DirectUpdateBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        UpdateBtnIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowSync24;
        UpdateBtnIcon.Visibility = Visibility.Visible;
        UpdateBtnRing.Visibility = Visibility.Collapsed;
        UpdateBtnText.Text = loc["General_CheckUpdatesBtn"];
        DirectUpdateBtn.ToolTip = loc["General_Updates_Desc"];
    }

    private async void OnDirectUpdateBtnClicked(object sender, RoutedEventArgs e)
    {
        if (_isCheckingOrUpdating) return;

        var loc = SanmiToys.Core.Services.LocalizationService.Instance;
        _isCheckingOrUpdating = true;
        DirectUpdateBtn.IsEnabled = false;

        try
        {
            if (_hasUpdateAvailable)
            {
                // すでに検知済みの更新をワンクリックでダウンロード・適用 / ブラウザオープン
                UpdateBtnIcon.Visibility = Visibility.Collapsed;
                UpdateBtnRing.Visibility = Visibility.Visible;
                UpdateBtnText.Text = loc["Nav_ApplyingUpdate"];

                if (UpdateService.Instance.IsVelopackInstalled)
                {
                    bool success = await UpdateService.Instance.DownloadAndApplyVelopackUpdateAsync(progress =>
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            UpdateBtnText.Text = string.Format(loc["Nav_UpdatingProgress"], progress);
                        });
                    });

                    if (!success)
                    {
                        UpdateBtnText.Text = loc["Nav_UpdateFailed"];
                        await Task.Delay(2500);
                        NavigateToModule("GeneralSettings");
                    }
                }
                else
                {
                    // ポータブル版 / 開発環境時は Releases ページを開く
                    string url = !string.IsNullOrEmpty(_updateReleaseUrl)
                        ? _updateReleaseUrl
                        : $"https://github.com/{UpdateService.DefaultGitHubRepo}/releases";

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("MainWindow", $"Failed to open release URL: {ex.Message}");
                    }

                    UpdateBtnText.Text = loc["Nav_ReleasePageOpened"];
                    await Task.Delay(3000);
                }
            }
            else
            {
                // 更新の検知チェックを実行
                UpdateBtnIcon.Visibility = Visibility.Collapsed;
                UpdateBtnRing.Visibility = Visibility.Visible;
                UpdateBtnText.Text = loc["General_CheckingUpdates"];

                var checkResult = await UpdateService.Instance.CheckForUpdatesAsync();

                if (checkResult.HasUpdate)
                {
                    _hasUpdateAvailable = true;
                    _latestDetectedVersion = checkResult.LatestVersion;
                    _updateReleaseUrl = checkResult.ReleaseUrl;

                    ApplyUpdateAvailableUI();
                }
                else
                {
                    UpdateBtnRing.Visibility = Visibility.Collapsed;
                    UpdateBtnIcon.Visibility = Visibility.Visible;

                    if (!string.IsNullOrEmpty(checkResult.ErrorMessage))
                    {
                        UpdateBtnIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
                        UpdateBtnText.Text = loc["Nav_CheckFailed"];
                    }
                    else
                    {
                        UpdateBtnIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24;
                        UpdateBtnText.Text = loc["Nav_UpToDateBadge"];
                    }

                    await Task.Delay(3000);
                    ResetUpdateBtnToDefault();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow", $"OnDirectUpdateBtnClicked exception: {ex.Message}");
            UpdateBtnText.Text = loc["Nav_CheckFailed"];
            await Task.Delay(2500);
            ResetUpdateBtnToDefault();
        }
        finally
        {
            _isCheckingOrUpdating = false;
            DirectUpdateBtn.IsEnabled = true;
            if (_hasUpdateAvailable)
            {
                ApplyUpdateAvailableUI();
            }
            else
            {
                ResetUpdateBtnToDefault();
            }
        }
    }

    public void ShowWindow()
    {
        this.Show();
        if (this.WindowState == WindowState.Minimized)
        {
            this.WindowState = WindowState.Normal;
        }
        this.Activate();
    }

    public void ExitApplication()
    {
        _isRealExit = true;
        _trayService.Dispose();
        foreach (var mod in _modules)
        {
            mod.Stop();
        }
        System.Windows.Application.Current.Shutdown();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isRealExit)
        {
            e.Cancel = true;
            this.Hide();
        }
    }

    public void NavigateToModule(string moduleId)
    {
        Type? targetType = moduleId switch
        {
            "FluidDrag" => typeof(FluidDragPage),
            "FocusDimmer" => typeof(FocusDimmerPage),
            "SnapTrans" => typeof(SnapTransPage),
            "SwiftVolume" => typeof(SwiftVolumePage),
            "OmniGlance" => typeof(OmniGlancePage),
            "GeneralSettings" => typeof(GeneralSettingsPage),
            _ => typeof(DashboardPage)
        };

        try
        {
            RootNav.Navigate(targetType);
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("Host", $"NavigateToModule error: {ex.Message}");
        }
    }

    public void RefreshDashboardState()
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_pageProvider.GetPage(typeof(DashboardPage)) is DashboardPage dp)
                {
                    dp.RefreshState();
                }

                if (RootNav.SelectedItem == null && RootNav.IsLoaded)
                {
                    RootNav.Navigate(typeof(DashboardPage));
                }
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("Host", $"RefreshDashboardState warning: {ex.Message}");
            }
        });
    }

    private void OnSupportHeartBtnClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var supportWin = new SupportWindow
            {
                Owner = this
            };
            supportWin.ShowDialog();
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("Host", $"SupportWindow open error: {ex.Message}");
        }
    }
}

