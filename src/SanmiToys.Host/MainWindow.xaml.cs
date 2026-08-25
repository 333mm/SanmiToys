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

    public SanmiToysPageProvider(List<IToyModule> modules, Action<string> navigateAction)
    {
        _modules = modules;
        _navigateAction = navigateAction;
    }

    public object? GetPage(Type pageType)
    {
        if (pageType == typeof(DashboardPage))
        {
            return new DashboardPage(_modules, _navigateAction);
        }
        if (pageType == typeof(GeneralSettingsPage))
        {
            return new GeneralSettingsPage();
        }
        if (pageType == typeof(FluidDragPage))
        {
            var mod = _modules.Find(m => m.Id == "FluidDrag");
            return mod != null ? new FluidDragPage(mod) : null;
        }
        if (pageType == typeof(FocusDimmerPage))
        {
            var mod = _modules.Find(m => m.Id == "FocusDimmer");
            return mod != null ? new FocusDimmerPage(mod) : null;
        }
        if (pageType == typeof(SnapTransPage))
        {
            var mod = _modules.Find(m => m.Id == "SnapTrans");
            return mod != null ? new SnapTransPage(mod) : null;
        }
        if (pageType == typeof(SwiftVolumePage))
        {
            var mod = _modules.Find(m => m.Id == "SwiftVolume");
            return mod != null ? new SwiftVolumePage(mod) : null;
        }
        return null;
    }
}

public partial class MainWindow : FluentWindow
{
    private readonly List<IToyModule> _modules;
    private readonly TrayIconService _trayService;
    private bool _isRealExit = false;

    private string _latestDetectedVersion = string.Empty;

    public MainWindow(List<IToyModule> modules)
    {
        InitializeComponent();
        _modules = modules;

        RootNav.SetPageProviderService(new SanmiToysPageProvider(_modules, NavigateToModule));

        _trayService = new TrayIconService(_modules, ShowWindow, ExitApplication);

        UpdateService.Instance.StartPeriodicUpdateCheck(result =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _latestDetectedVersion = result.LatestVersion;
                var loc = SanmiToys.Core.Services.LocalizationService.Instance;
                string title = loc["Nav_UpdateNotificationTitle"];
                string msg = string.Format(loc["Nav_UpdateNotificationBody"], result.LatestVersion);
                _trayService.ShowBalloonTip(title, msg);

                UpdateLocalizedBadgeText();
                UpdateBadgeContainer.Visibility = Visibility.Visible;
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
            RootNav.Navigate(typeof(DashboardPage));
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

        RootNav.Navigate(targetType);
    }

    public void RefreshDashboardState()
    {
        Dispatcher.InvokeAsync(() =>
        {
            RootNav.Navigate(typeof(DashboardPage));
        });
    }
}
