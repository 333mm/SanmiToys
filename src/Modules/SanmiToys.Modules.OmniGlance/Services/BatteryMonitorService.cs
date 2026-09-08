using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

namespace SanmiToys.Modules.OmniGlance.Services;

public class BatteryMonitorService : IDisposable
{
    private readonly Func<OmniGlanceSettings> _getSettings;
    private readonly List<IBatteryProvider> _providers = new();
    private readonly DemoBatteryProvider _demoProvider = new();
    private System.Threading.Timer? _scanTimer;
    private bool _isScanning;

    public ObservableCollection<DeviceBatteryInfo> Devices { get; } = new();

    public event Action<DeviceBatteryInfo>? LowBatteryAlertTriggered;

    public void TriggerAlert(DeviceBatteryInfo device) => LowBatteryAlertTriggered?.Invoke(device);

    public BatteryMonitorService(Func<OmniGlanceSettings> getSettings)
    {
        _getSettings = getSettings;

        // 各種プロバイダーを登録
        _providers.Add(new SystemPowerBatteryProvider());
        _providers.Add(new BluetoothGattBatteryProvider());
        _providers.Add(new LogitechHidBatteryProvider());
        _providers.Add(new RazerHidBatteryProvider());
        _providers.Add(new WebHidGenericBatteryProvider());
        _providers.Add(new PhoneLinkBatteryProvider());
    }

    public void Start(int intervalMs = 12000)
    {
        Stop();
        _scanTimer = new System.Threading.Timer(async _ => await ScanAsync(), null, 500, intervalMs);
    }

    public void Stop()
    {
        _scanTimer?.Dispose();
        _scanTimer = null;
    }

    public async Task ScanAsync()
    {
        if (_isScanning) return;
        _isScanning = true;

        try
        {
            var settings = _getSettings();
            var detectedMap = new Dictionary<string, DeviceBatteryInfo>();

            // 各プロバイダーを順次スキャン
            foreach (var provider in _providers)
            {
                try
                {
                    var devList = await provider.GetDevicesAsync(settings);
                    foreach (var d in devList)
                    {
                        // カテゴリ・ID・名前による重複除外
                        string key = $"{d.Category}_{d.Id}_{d.Name}".ToLowerInvariant();
                        if (!detectedMap.ContainsKey(key))
                        {
                            detectedMap[key] = d;
                        }
                    }
                }
                catch { }
            }

            // デモ表示が有効な場合
            if (settings.EnableDemoDevices)
            {
                var demos = await _demoProvider.GetDevicesAsync(settings);
                foreach (var d in demos)
                {
                    string key = $"{d.Category}_{d.Id}_{d.Name}".ToLowerInvariant();
                    if (!detectedMap.ContainsKey(key))
                    {
                        detectedMap[key] = d;
                    }
                }
            }

            var detected = detectedMap.Values.ToList();

            Action applyAction = () =>
            {
                var toRemove = Devices.Where(d => detected.All(x => x.Id != d.Id && x.Name != d.Name)).ToList();
                foreach (var rem in toRemove) Devices.Remove(rem);

                foreach (var det in detected)
                {
                    var existing = Devices.FirstOrDefault(d => d.Id == det.Id || d.Name == det.Name);
                    if (existing != null)
                    {
                        existing.Name = det.Name;
                        existing.Category = det.Category;
                        existing.BatteryLevel = det.BatteryLevel;
                        existing.IsCharging = det.IsCharging;
                        existing.IsConnected = det.IsConnected;
                        existing.IsLowBattery = det.IsLowBattery;
                        existing.IsCriticalBattery = det.IsCriticalBattery;
                        existing.IsOptionEnabled = !settings.DisabledDeviceIds.Contains(existing.Id) && !settings.DisabledDeviceIds.Contains(existing.Name);

                        CheckAndTriggerAlert(existing, settings, LowBatteryAlertTriggered);
                    }
                    else
                    {
                        det.IsOptionEnabled = !settings.DisabledDeviceIds.Contains(det.Id) && !settings.DisabledDeviceIds.Contains(det.Name);
                        Devices.Add(det);
                        CheckAndTriggerAlert(det, settings, LowBatteryAlertTriggered);
                    }
                }
            };

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                await app.Dispatcher.InvokeAsync(applyAction);
            }
            else
            {
                applyAction();
            }
        }
        catch { }
        finally
        {
            _isScanning = false;
        }
    }

    private static bool CheckAndTriggerAlert(DeviceBatteryInfo device, OmniGlanceSettings settings, Action<DeviceBatteryInfo>? alertCallback)
    {
        if (device.IsCharging)
        {
            // 充電中で残量がしきい値を超えて回復した場合はアラート履歴をリセット
            if (device.BatteryLevel > settings.LowBatteryThreshold)
            {
                device.LastAlertedLevel = -1;
            }
            return false;
        }

        int level = device.BatteryLevel;
        if (level > settings.LowBatteryThreshold)
        {
            device.LastAlertedLevel = -1;
            return false;
        }

        int last = device.LastAlertedLevel;
        bool shouldTrigger = false;

        if (last < 0)
        {
            // 初回（警告しきい値以下に突入時）
            shouldTrigger = true;
        }
        else if (level <= 10 && last > 10)
        {
            // 10% マイルストーン
            shouldTrigger = true;
        }
        else if (level <= 5 && last > 5)
        {
            // 5% マイルストーン
            shouldTrigger = true;
        }
        else if (level < 5 && level < last)
        {
            // 5% 以下は 1% 低下するごとに再表示 (4%, 3%, 2%, 1%, 0%)
            shouldTrigger = true;
        }
        else if (level > last)
        {
            // 残量が増加（一時的復帰や充電等）した場合は履歴を更新
            device.LastAlertedLevel = level;
        }

        if (shouldTrigger)
        {
            device.LastAlertedLevel = level;
            alertCallback?.Invoke(device);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}
