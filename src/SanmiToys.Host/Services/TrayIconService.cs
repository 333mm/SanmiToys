using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using SanmiToys.Core.Interfaces;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using PlacementMode = System.Windows.Controls.Primitives.PlacementMode;

namespace SanmiToys.Host.Services;

public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly List<IToyModule> _modules;
    private readonly Action _showMainWindowAction;
    private readonly Action _exitAppAction;
    private readonly ContextMenu _contextMenu;

    public TrayIconService(List<IToyModule> modules, Action showMainWindowAction, Action exitAppAction)
    {
        _modules = modules;
        _showMainWindowAction = showMainWindowAction;
        _exitAppAction = exitAppAction;

        Icon trayIcon = SystemIcons.Application;
        try
        {
            var uri = new Uri("pack://application:,,,/SanmiToys.Host;component/Assets/app.ico", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                trayIcon = new Icon(streamInfo.Stream);
            }
            else
            {
                string localIco = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
                if (System.IO.File.Exists(localIco))
                {
                    trayIcon = new Icon(localIco);
                }
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _contextMenu = new ContextMenu { Placement = PlacementMode.MousePoint };

        // アイコン登録は WinForms NotifyIcon を使う。アプリ起動のごく早い段階でも
        // Explorer への登録が安定しており、WPF メニューは右クリック時に別途表示する。
        _notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "SanmiToys",
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => _showMainWindowAction();
        _notifyIcon.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                BuildContextMenu();
                _contextMenu.IsOpen = true;
            });
        };
        _notifyIcon.BalloonTipClicked += (s, e) =>
        {
            _showMainWindowAction();
        };

        SanmiToys.Core.Services.LocalizationService.Instance.LanguageChanged += UpdateMenuState;
        BuildContextMenu();
    }

    public void ShowBalloonTip(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, int timeoutMs = 5000)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(timeoutMs, title, text, icon);
        }
        catch { }
    }

    public void BuildContextMenu()
    {
        _contextMenu.Items.Clear();
        var loc = SanmiToys.Core.Services.LocalizationService.Instance;

        var titleItem = new MenuItem
        {
            Header = "SanmiToys",
            FontWeight = FontWeights.SemiBold,
            IsEnabled = false
        };
        _contextMenu.Items.Add(titleItem);
        _contextMenu.Items.Add(new Separator());

        foreach (var module in _modules)
        {
            var moduleItem = new MenuItem
            {
                Header = module.Name,
                IsCheckable = true,
                IsChecked = module.IsEnabled
            };
            moduleItem.Click += (s, e) =>
            {
                module.IsEnabled = moduleItem.IsChecked;
            };
            _contextMenu.Items.Add(moduleItem);
        }

        _contextMenu.Items.Add(new Separator());

        var settingsItem = new MenuItem { Header = loc["Tray_OpenDashboard"] };
        settingsItem.Click += (s, e) => _showMainWindowAction();
        _contextMenu.Items.Add(settingsItem);

        var exitItem = new MenuItem { Header = loc["Tray_Exit"] };
        exitItem.Click += (s, e) => _exitAppAction();
        _contextMenu.Items.Add(exitItem);
    }

    public void UpdateMenuState()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(BuildContextMenu);
    }

    public void Dispose()
    {
        SanmiToys.Core.Services.LocalizationService.Instance.LanguageChanged -= UpdateMenuState;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
