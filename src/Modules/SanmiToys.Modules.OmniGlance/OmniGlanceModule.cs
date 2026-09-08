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
        _settings.IsEnabled = _settingsService.IsModuleEnabled(Id, _settings.IsEnabled);
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
        TriggerTestBatteryAlert();
    }

    public void TriggerTestBatteryAlert()
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

            _batteryService?.TriggerAlert(testDev);
        });
    }

    public void TriggerTestCpuTempAlert()
    {
        RunOnUi(() =>
        {
            _islandWindow?.TriggerAlert(new IslandAlertInfo
            {
                Type = IslandAlertType.CpuTemperature,
                Title = "CPU 高温警告",
                Message = "CPU温度が危険域に達しています (88°C)",
                LevelText = "88°C",
                BadgeText = "HOT",
                Symbol = Wpf.Ui.Controls.SymbolRegular.DeveloperBoard20,
                AlertColor = System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x4F)
            });
        });
    }

    public void TriggerTestGpuTempAlert()
    {
        RunOnUi(() =>
        {
            _islandWindow?.TriggerAlert(new IslandAlertInfo
            {
                Type = IslandAlertType.GpuTemperature,
                Title = "GPU 高温警告",
                Message = "GPU温度が危険域に達しています (85°C)",
                LevelText = "85°C",
                BadgeText = "HOT",
                Symbol = Wpf.Ui.Controls.SymbolRegular.WindowDevTools20,
                AlertColor = System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x4F)
            });
        });
    }

    public void TriggerTestMemoryAlert()
    {
        RunOnUi(() =>
        {
            _islandWindow?.TriggerAlert(new IslandAlertInfo
            {
                Type = IslandAlertType.MemoryUsage,
                Title = "メモリ使用量警告",
                Message = "システムメモリが逼迫しています (94%)",
                LevelText = "94%",
                BadgeText = "FULL",
                Symbol = Wpf.Ui.Controls.SymbolRegular.Ram20,
                AlertColor = System.Windows.Media.Color.FromRgb(0xFF, 0xA9, 0x40)
            });
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
