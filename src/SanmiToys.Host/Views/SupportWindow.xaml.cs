using System;
using System.Diagnostics;
using System.Windows;
using SanmiToys.Core.Services;
using SanmiToys.Host.Services;
using Wpf.Ui.Controls;

namespace SanmiToys.Host.Views;

public partial class SupportWindow : FluentWindow
{
    public const string DONATE_OFUSE_URL = "https://ofuse.me/d3a3316d";
    public const string DONATE_BUYMEACOFFEE_URL = "https://buymeacoffee.com/sanmi";
    public const string STORE_PRODUCT_ID = "9NQDSVBDSS3M";
    public const string STORE_ADDON_URI = "ms-windows-store://pdp/?productid=9NQDSVBDSS3M";

    public SupportWindow()
    {
        InitializeComponent();

        var isStore = UpdateService.IsRunningAsPackagedStoreApp();
        if (isStore)
        {
            ExeSupportPanel.Visibility = Visibility.Collapsed;
            StoreSupportPanel.Visibility = Visibility.Visible;
            EditionBadgeText.Text = LocalizationService.Instance["Support_Mode_Store_Badge"];
        }
        else
        {
            ExeSupportPanel.Visibility = Visibility.Visible;
            StoreSupportPanel.Visibility = Visibility.Collapsed;
            EditionBadgeText.Text = LocalizationService.Instance["Support_Mode_Exe_Badge"];
        }
    }

    private void OnOfuseClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl(DONATE_OFUSE_URL);
    }

    private void OnBuyMeACoffeeClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl(DONATE_BUYMEACOFFEE_URL);
    }

    private void OnStoreAddonClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl(STORE_ADDON_URI);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
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
            Debug.WriteLine($"Failed to open URL {url}: {ex.Message}");
        }
    }
}
