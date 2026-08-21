using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SanmiToys.Core.Interfaces;

namespace SanmiToys.Host.Services;

public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly List<IToyModule> _modules;
    private readonly Action _showMainWindowAction;
    private readonly Action _exitAppAction;

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

        _notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "SanmiToys",
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => _showMainWindowAction();
        SanmiToys.Core.Services.LocalizationService.Instance.LanguageChanged += UpdateMenuState;
        BuildContextMenu();
    }

    public void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var titleItem = new ToolStripMenuItem("SanmiToys")
        {
            Font = new Font(Control.DefaultFont, FontStyle.Bold),
            Enabled = false
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        foreach (var module in _modules)
        {
            var moduleItem = new ToolStripMenuItem($"{module.Name}")
            {
                Checked = module.IsEnabled,
                CheckOnClick = true
            };
            moduleItem.Click += (s, e) =>
            {
                module.IsEnabled = moduleItem.Checked;
            };
            menu.Items.Add(moduleItem);
        }

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem(SanmiToys.Core.Services.LocalizationService.Instance["Tray_OpenDashboard"], null, (s, e) => _showMainWindowAction());
        menu.Items.Add(settingsItem);

        var exitItem = new ToolStripMenuItem(SanmiToys.Core.Services.LocalizationService.Instance["Tray_Exit"], null, (s, e) => _exitAppAction());
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
    }

    public void UpdateMenuState()
    {
        if (_notifyIcon.ContextMenuStrip == null) return;
        BuildContextMenu();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
