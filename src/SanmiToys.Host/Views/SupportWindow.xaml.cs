using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using SanmiToys.Core.Services;
using SanmiToys.Host.Services;
using Wpf.Ui.Controls;

namespace SanmiToys.Host.Views;

public partial class SupportWindow : FluentWindow
{
    public const string SUPPORT_OFUSE_URL = "https://ofuse.me/d3a3316d";
    public const string SUPPORT_KOFI_URL = "https://ko-fi.com/sanmiri";

    public SupportWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        SanmiToys.Core.Helpers.DwmCompositionHelper.AttachEarly(this);
        base.OnSourceInitialized(e);
    }

    private void OnOfuseClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl(SUPPORT_OFUSE_URL);
    }

    private void OnKoFiClicked(object sender, RoutedEventArgs e)
    {
        OpenUrl(SUPPORT_KOFI_URL);
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
