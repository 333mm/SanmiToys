using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SanmiToys.Core.Services;
using SanmiToys.Host.Services;
using Wpf.Ui.Controls;

namespace SanmiToys.Host.Views;

public partial class GeneralSettingsPage : Page
{
    private bool _isInitializing = true;

    // ドネーション用URL
    private const string DONATE_OFUSE_URL = "https://ofuse.me";
    private const string DONATE_BUYMEACOFFEE_URL = "https://buymeacoffee.com";

    public GeneralSettingsPage()
    {
        InitializeComponent();

        // バージョン情報表示
        AppVersionText.Text = $"SanmiToys v{UpdateService.Instance.GetCurrentVersionString()}";

        // 言語一覧設定
        LanguageCombo.ItemsSource = LocalizationService.SupportedLanguages;
        var currentLang = LocalizationService.Instance.CurrentLanguageCode;
        var selectedOpt = LocalizationService.SupportedLanguages.FirstOrDefault(l => l.Code == currentLang) 
                          ?? LocalizationService.SupportedLanguages[0];
        LanguageCombo.SelectedItem = selectedOpt;

        // スタートアップ＆トレイ設定
        StartupSwitch.IsChecked = StartupManager.IsStartupEnabled();
        MinimizeToTraySwitch.IsChecked = SettingsService.Instance.GetGeneralSetting("MinimizeToTray", true);

        _isInitializing = false;
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (LanguageCombo.SelectedItem is LanguageOption option)
        {
            LocalizationService.Instance.CurrentLanguageCode = option.Code;
        }
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        StartupManager.SetStartup(StartupSwitch.IsChecked == true);
    }

    private void OnMinimizeToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.SetGeneralSetting("MinimizeToTray", MinimizeToTraySwitch.IsChecked == true);
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        CheckUpdatesBtn.IsEnabled = false;
        UpdateProgressRing.Visibility = Visibility.Visible;
        UpdateInfoBar.IsOpen = false;

        try
        {
            var result = await UpdateService.Instance.CheckForUpdatesAsync();

            UpdateInfoBar.IsOpen = true;
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Error;
                UpdateInfoBar.Title = LocalizationService.Instance["General_UpdatesSection"];
                UpdateInfoBar.Message = LocalizationService.Instance["General_UpdateError"];
            }
            else if (result.HasUpdate)
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Success;
                UpdateInfoBar.Title = LocalizationService.Instance["General_UpdatesSection"];
                UpdateInfoBar.Message = string.Format(LocalizationService.Instance["General_UpdateAvailable"], result.LatestVersion);

                if (!string.IsNullOrEmpty(result.ReleaseUrl))
                {
                    OpenUrl(result.ReleaseUrl);
                }
            }
            else
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Informational;
                UpdateInfoBar.Title = LocalizationService.Instance["General_UpdatesSection"];
                UpdateInfoBar.Message = string.Format(LocalizationService.Instance["General_UpToDate"], result.CurrentVersion);
            }
        }
        catch (Exception ex)
        {
            UpdateInfoBar.IsOpen = true;
            UpdateInfoBar.Severity = InfoBarSeverity.Error;
            UpdateInfoBar.Title = LocalizationService.Instance["General_UpdatesSection"];
            UpdateInfoBar.Message = ex.Message;
        }
        finally
        {
            CheckUpdatesBtn.IsEnabled = true;
            UpdateProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDonateOfuseClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl(DONATE_OFUSE_URL);
    }

    private void OnDonateBuyMeACoffeeClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl(DONATE_BUYMEACOFFEE_URL);
    }

    private static void OpenUrl(string url)
    {
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
            System.Diagnostics.Debug.WriteLine($"Failed to open URL {url}: {ex.Message}");
        }
    }
}
