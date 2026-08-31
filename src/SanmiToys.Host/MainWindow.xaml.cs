using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using SanmiToys.Core.Interfaces;
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

    private string _latestDetectedVersion = string.Empty;

    public MainWindow(List<IToyModule> modules)
    {
        InitializeComponent();
        _modules = modules;

        _pageProvider = new SanmiToysPageProvider(_modules, NavigateToModule);
        RootNav.SetPageProviderService(_pageProvider);

        _trayService = new TrayIconService(_modules, ShowWindow, ExitApplication);

        UpdateService.Instance.StartPeriodicUpdateCheck(result =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _latestDetectedVersion = result.LatestVersion;
                var loc = SanmiToys.Core.Services.LocalizationService.Instance;
                string title = loc["Nav_UpdateNotificationTitle"];
                string msg = string.Format(loc["Nav_UpdateNotificationBody"], result.LatestVersion);

                // Windows 通知オプション判定 (設定で有効な場合のみトースト通知)
                bool notifyEnabled = SanmiToys.Core.Services.SettingsService.Instance.GetGeneralSetting("NotifyOnUpdate", true);
                if (notifyEnabled)
                {
                    _trayService.ShowBalloonTip(title, msg);
                }

                UpdateLocalizedBadgeText();
                UpdateBadgeContainer.Visibility = Visibility.Visible;

                // アップデートがあるときは起動時（検知時）にダッシュボードを表示
                ShowWindow();
                try
                {
                    RootNav.Navigate(typeof(DashboardPage));
                }
                catch { }
            });
        }, TimeSpan.FromHours(1));

        SanmiToys.Core.Services.LocalizationService.Instance.LanguageChanged += () =>
        {
            Dispatcher.InvokeAsync(UpdateLocalizedBadgeText);
        };

#if DEBUG
        // VSのデバッグ構成では常に閲覧・テスト可能にする
        UpdateLocalizedBadgeText();
        UpdateBadgeContainer.Visibility = Visibility.Visible;
#endif

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

    private void UpdateLocalizedBadgeText()
    {
        var loc = SanmiToys.Core.Services.LocalizationService.Instance;
        if (!string.IsNullOrEmpty(_latestDetectedVersion))
        {
            UpdateBtnText.Text = string.Format(loc["Nav_UpdateVersion"], _latestDetectedVersion);
        }
        else
        {
#if DEBUG
            UpdateBtnText.Text = $"{loc["Nav_UpdateNow"]} (Debug)";
#else
            UpdateBtnText.Text = loc["Nav_UpdateNow"];
#endif
        }
    }

    private async void OnDirectUpdateBtnClicked(object sender, RoutedEventArgs e)
    {
        DirectUpdateBtn.IsEnabled = false;
        UpdateBtnIcon.Visibility = Visibility.Collapsed;
        UpdateBtnRing.Visibility = Visibility.Visible;

        var loc = SanmiToys.Core.Services.LocalizationService.Instance;
        UpdateBtnText.Text = loc["Nav_ApplyingUpdate"];

        try
        {
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
                    NavigateToModule("GeneralSettings");
                }
            }
            else
            {
                var checkResult = await UpdateService.Instance.CheckForUpdatesAsync();
                string url = !string.IsNullOrEmpty(checkResult.ReleaseUrl) 
                    ? checkResult.ReleaseUrl 
                    : $"https://github.com/{UpdateService.DefaultGitHubRepo}/releases";

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch { }

                UpdateBtnText.Text = loc.EffectiveLanguageCode == "ja" ? "ページを開きました" : "Opened release page";
                await System.Threading.Tasks.Task.Delay(3000);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Update Error] {ex.Message}");
            NavigateToModule("GeneralSettings");
        }
        finally
        {
            DirectUpdateBtn.IsEnabled = true;
            UpdateBtnIcon.Visibility = Visibility.Visible;
            UpdateBtnRing.Visibility = Visibility.Collapsed;
            UpdateLocalizedBadgeText();
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
}
