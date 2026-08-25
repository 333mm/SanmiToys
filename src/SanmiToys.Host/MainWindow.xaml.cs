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
                bool isJa = SanmiToys.Core.Services.LocalizationService.Instance.EffectiveLanguageCode == "ja";
                string title = isJa ? "SanmiToys アップデート" : "SanmiToys Update";
                string msg = isJa
                    ? $"新しいバージョン ({result.LatestVersion}) が利用可能です。\nクリックしてアップデートを開きます。"
                    : $"A new version ({result.LatestVersion}) is available.\nClick to view update.";
                _trayService.ShowBalloonTip(title, msg);

                UpdateBtnText.Text = isJa ? $"更新 (v{result.LatestVersion})" : $"Update (v{result.LatestVersion})";
                UpdateBadgeContainer.Visibility = Visibility.Visible;
            });
        }, TimeSpan.FromHours(1));

#if DEBUG
        // VSのデバッグ構成では常に閲覧・テスト可能にする
        UpdateBtnText.Text = "今すぐ更新 (Debug)";
        UpdateBadgeContainer.Visibility = Visibility.Visible;
#endif

        this.Loaded += (s, e) =>
        {
            RootNav.Navigate(typeof(DashboardPage));
        };

        this.Closing += OnWindowClosing;
    }

    private async void OnDirectUpdateBtnClicked(object sender, RoutedEventArgs e)
    {
        DirectUpdateBtn.IsEnabled = false;
        UpdateBtnIcon.Visibility = Visibility.Collapsed;
        UpdateBtnRing.Visibility = Visibility.Visible;

        bool isJa = SanmiToys.Core.Services.LocalizationService.Instance.EffectiveLanguageCode == "ja";
        UpdateBtnText.Text = isJa ? "更新を適用中..." : "Updating...";

        try
        {
            if (UpdateService.Instance.IsVelopackInstalled)
            {
                bool success = await UpdateService.Instance.DownloadAndApplyVelopackUpdateAsync(progress =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        UpdateBtnText.Text = isJa ? $"更新中 {progress}%" : $"Updating {progress}%";
                    });
                });

                if (!success)
                {
                    // 適用失敗または利用不可時はブラウザで Releases を開く
                    NavigateToModule("GeneralSettings");
                }
            }
            else
            {
                // ポータブル版 / 開発環境時は Releases ページを開く
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

                UpdateBtnText.Text = isJa ? "ページを開きました" : "Opened release page";
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
#if DEBUG
            UpdateBtnText.Text = "今すぐ更新 (Debug)";
#endif
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
