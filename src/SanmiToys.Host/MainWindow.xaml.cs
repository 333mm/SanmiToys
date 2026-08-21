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

        this.Loaded += (s, e) =>
        {
            RootNav.Navigate(typeof(DashboardPage));
        };

        this.Closing += OnWindowClosing;
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
