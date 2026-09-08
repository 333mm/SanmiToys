using System;
using System.Threading.Tasks;
using System.Windows;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using SanmiToys.Modules.OmniGlance.Models;
using SanmiToys.Modules.OmniGlance.Services;
using SanmiToys.Modules.OmniGlance.Views;

namespace SanmiToys.Modules.OmniGlance;

public class OmniGlanceModule : IToyModule
{
    private readonly SettingsService _settingsService;
    private readonly Action<string>? _navigateAction;
    private OmniGlanceSettings _settings;
    private PerformanceMonitorService? _perfService;
    private BatteryMonitorService? _batteryService;
    private CalendarSyncService? _calendarService;
    private OmniIslandWindow? _islandWindow;

    public string Id => "OmniGlance";
    public string Name => "OmniGlance";
    public string Description => LocalizationService.Instance["OmniGlance_Desc"];
    public string IconGlyph => "\uE7C4"; // Glance / Status icon

    public OmniGlanceSettings Settings => _settings;
    public CalendarSyncService? CalendarService => _calendarService;
    public BatteryMonitorService? BatteryService => _batteryService;

    public bool IsEnabled
    {
        get => _settings.IsEnabled;
        set
        {
            if (_settings.IsEnabled != value)
            {
                _settings.IsEnabled = value;
                _settingsService.SetModuleSettings(Id, _settings);
                _settingsService.SetModuleEnabled(Id, value);
                if (value) Start(); else Stop();
            }
        }
    }

    public OmniGlanceModule(SettingsService settingsService, Action<string>? navigateAction = null)
    {
        _settingsService = settingsService;
        _navigateAction = navigateAction;
        _settings = _settingsService.GetModuleSettings<OmniGlanceSettings>(Id);
        _settings.IsEnabled = _settingsService.IsModuleEnabled(Id, true);
    }

    public Task InitializeAsync()
    {
        _perfService = new PerformanceMonitorService(() => _settings);
        _batteryService = new BatteryMonitorService(() => _settings);
        _calendarService = new CalendarSyncService(() => _settings, s =>
        {
            _settings = s;
            SaveSettings();
        });

        if (_settings.IsEnabled)
        {
            Start();
        }

        return Task.CompletedTask;
    }

    public void Start()
    {
        _perfService?.Start();
        _batteryService?.Start();
        _calendarService?.Start();

        RunOnUi(() =>
        {
            if (_islandWindow == null && _perfService != null && _batteryService != null)
            {
                _islandWindow = new OmniIslandWindow(
                    () => _settings,
                    s => SaveSettings(),
                    _perfService,
                    _batteryService,
                    _calendarService,
                    _navigateAction);
            }
            _islandWindow?.Show();
            _islandWindow?.ApplySettings();
        });
    }

    public void Stop()
    {
        _perfService?.Stop();
        _batteryService?.Stop();
        _calendarService?.Stop();

        RunOnUi(() =>
        {
            _islandWindow?.Hide();
        });
    }

    public void SaveSettings()
    {
        _settingsService.SetModuleSettings(Id, _settings);
        _settingsService.SetModuleEnabled(Id, _settings.IsEnabled);
    }

    public void UpdateOverlay()
    {
        RunOnUi(() =>
        {
            _islandWindow?.ApplySettings();
        });
    }

    public Task RefreshBatteryScanAsync()
    {
        return _batteryService?.ScanAsync() ?? Task.CompletedTask;
    }

    public Task SyncCalendarAsync()
    {
        return _calendarService?.SyncAsync() ?? Task.CompletedTask;
    }

    public void TriggerTestAlert()
    {
        RunOnUi(() =>
        {
            var testDev = new DeviceBatteryInfo
            {
                Id = "TEST_DEVICE",
                Name = "Wireless Studio Headset",
                Category = DeviceCategory.Headset,
                BatteryLevel = 9,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = true
            };

            // 低バッテリーアラート発火
            _batteryService?.TriggerAlert(testDev);
        });
    }

    public object? CreateSettingsView()
    {
        return new OmniGlanceSettingsView(this);
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher == null) return;
        if (app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.InvokeAsync(action);
        }
    }
}
