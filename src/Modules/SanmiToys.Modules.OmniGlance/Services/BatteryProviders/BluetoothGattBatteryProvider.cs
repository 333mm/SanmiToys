using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class BluetoothGattBatteryProvider : IBatteryProvider, IDisposable
{
    private const string PROP_BATTERY_PERCENTAGE = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PROP_AEP_IS_CONNECTED = "System.Devices.Aep.IsConnected";
    private const string PROP_AEP_IS_PAIRED = "System.Devices.Aep.IsPaired";
    private const string PROP_ITEM_NAME_DISPLAY = "System.ItemNameDisplay";

    public string ProviderName => "BluetoothGatt";

    public event Action? DevicesChanged;

    private DeviceWatcher? _aepWatcher;
    private DeviceWatcher? _batWatcher;
    private System.Threading.Timer? _debounceTimer;
    private readonly object _lock = new();
    private bool _isDisposed;

    public BluetoothGattBatteryProvider()
    {
        InitializeWatchers();
    }

    private void InitializeWatchers()
    {
        try
        {
            // 1. Bluetooth AssociationEndpoint Watcher (新規ペアリング・追加・接続・切断をリアルタイム監視)
            string aepAqs = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd35-4d06-bb8d-2ac5d16ac874}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
            var requestedProps = new[] { PROP_BATTERY_PERCENTAGE, PROP_ITEM_NAME_DISPLAY, PROP_AEP_IS_CONNECTED, PROP_AEP_IS_PAIRED };

            _aepWatcher = DeviceInformation.CreateWatcher(aepAqs, requestedProps, DeviceInformationKind.AssociationEndpoint);
            _aepWatcher.Added += OnDeviceWatcherEvent;
            _aepWatcher.Updated += OnDeviceWatcherEvent;
            _aepWatcher.Removed += OnDeviceWatcherEvent;
            _aepWatcher.Start();

            // 2. Windows BluetoothBattery Device Interface Watcher (オーディオ機器等のバッテリーインターフェース監視)
            string batAqs = "System.Devices.InterfaceClassGuid:=\"{45BD510D-AE3A-4D0C-B3F3-2BF7F12E0995}\"";
            _batWatcher = DeviceInformation.CreateWatcher(batAqs, new[] { PROP_BATTERY_PERCENTAGE, PROP_ITEM_NAME_DISPLAY, PROP_AEP_IS_CONNECTED });
            _batWatcher.Added += OnDeviceWatcherEvent;
            _batWatcher.Updated += OnDeviceWatcherEvent;
            _batWatcher.Removed += OnDeviceWatcherEvent;
            _batWatcher.Start();
        }
        catch { }
    }

    private void OnDeviceWatcherEvent(DeviceWatcher sender, object args)
    {
        if (_isDisposed) return;

        lock (_lock)
        {
            if (_isDisposed) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                if (!_isDisposed)
                {
                    DevicesChanged?.Invoke();
                }
            }, null, 350, Timeout.Infinite);
        }
    }

    public async Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Windows BluetoothBattery Device Interface (GUID_DEVINTERFACE_BLUETOOTH_BATTERY)
        try
        {
            string batSelector = "System.Devices.InterfaceClassGuid:=\"{45BD510D-AE3A-4D0C-B3F3-2BF7F12E0995}\"";
            var batDevices = await DeviceInformation.FindAllAsync(batSelector, new[] { PROP_BATTERY_PERCENTAGE, PROP_ITEM_NAME_DISPLAY, PROP_AEP_IS_CONNECTED }).AsTask();
            foreach (var di in batDevices)
            {
                try
                {
                    if (TryExtractBatteryLevel(di, out int level))
                    {
                        string name = GetDeviceName(di, string.Empty);
                        string id = $"BT_BAT_{di.Id}";
                        if (seenIds.Add(di.Id))
                        {
                            if (!string.IsNullOrWhiteSpace(name)) seenNames.Add(name);
                            result.Add(CreateDeviceInfo(id, string.IsNullOrWhiteSpace(name) ? "Bluetooth Device" : name, level, settings, di));
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        // 2. クラシック Bluetooth ペアリング済みデバイス (BluetoothDevice)
        try
        {
            string classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var classicDevices = await DeviceInformation.FindAllAsync(classicSelector, new[] { PROP_BATTERY_PERCENTAGE, PROP_ITEM_NAME_DISPLAY, PROP_AEP_IS_CONNECTED }, DeviceInformationKind.AssociationEndpoint).AsTask();
            foreach (var di in classicDevices)
            {
                try
                {
                    string name = GetDeviceName(di, string.Empty);
                    if (!string.IsNullOrWhiteSpace(name) && seenNames.Contains(name)) continue;
                    if (seenIds.Contains(di.Id)) continue;

                    if (TryExtractBatteryLevel(di, out int level))
                    {
                        seenIds.Add(di.Id);
                        if (!string.IsNullOrWhiteSpace(name)) seenNames.Add(name);
                        result.Add(CreateDeviceInfo($"BT_CLASSIC_{di.Id}", string.IsNullOrWhiteSpace(name) ? "Bluetooth Device" : name, level, settings, di));
                    }
                }
                catch { }
            }
        }
        catch { }

        // 3. Bluetooth Low Energy (BLE) ペアリング済みデバイス
        try
        {
            string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var bleDevices = await DeviceInformation.FindAllAsync(bleSelector, new[] { PROP_BATTERY_PERCENTAGE, PROP_ITEM_NAME_DISPLAY, PROP_AEP_IS_CONNECTED }, DeviceInformationKind.AssociationEndpoint).AsTask();

            foreach (var di in bleDevices)
            {
                try
                {
                    string name = GetDeviceName(di, string.Empty);
                    if (!string.IsNullOrWhiteSpace(name) && seenNames.Contains(name)) continue;
                    if (seenIds.Contains(di.Id)) continue;

                    // まず PnP プロパティから取得を試みる
                    if (TryExtractBatteryLevel(di, out int level))
                    {
                        seenIds.Add(di.Id);
                        if (!string.IsNullOrWhiteSpace(name)) seenNames.Add(name);
                        result.Add(CreateDeviceInfo($"BLE_{di.Id}", string.IsNullOrWhiteSpace(name) ? "Wireless Device" : name, level, settings, di));
                        continue;
                    }

                    // PnP プロパティになければ GATT Battery Service (0x180F) を試行 (タイムアウト付き)
                    int gattBattery = await ReadBleGattBatteryWithTimeoutAsync(di.Id, 1500);
                    if (gattBattery >= 0 && gattBattery <= 100)
                    {
                        seenIds.Add(di.Id);
                        if (!string.IsNullOrWhiteSpace(name)) seenNames.Add(name);
                        result.Add(CreateDeviceInfo($"BLE_{di.Id}", string.IsNullOrWhiteSpace(name) ? "Wireless Device" : name, gattBattery, settings, di));
                    }
                }
                catch { }
            }
        }
        catch { }

        return result;
    }

    private static async Task<int> ReadBleGattBatteryWithTimeoutAsync(string deviceId, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            return await Task.Run(async () =>
            {
                try
                {
                    using var ble = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask(cts.Token);
                    if (ble == null) return -1;

                    // Cached を優先試行
                    var gattServices = await ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(cts.Token);
                    if (gattServices.Status != GattCommunicationStatus.Success || gattServices.Services.Count == 0)
                    {
                        // 接続中であれば Uncached も試行
                        if (ble.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        {
                            gattServices = await ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask(cts.Token);
                        }
                    }

                    if (gattServices.Status == GattCommunicationStatus.Success)
                    {
                        foreach (var s in gattServices.Services)
                        {
                            var characteristics = await s.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask(cts.Token);
                            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                            {
                                if (ble.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                {
                                    characteristics = await s.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached).AsTask(cts.Token);
                                }
                            }

                            if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0)
                            {
                                var readResult = await characteristics.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Cached).AsTask(cts.Token);
                                if (readResult.Status != GattCommunicationStatus.Success || readResult.Value == null || readResult.Value.Length == 0)
                                {
                                    if (ble.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                    {
                                        readResult = await characteristics.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cts.Token);
                                    }
                                }

                                if (readResult.Status == GattCommunicationStatus.Success && readResult.Value != null && readResult.Value.Length > 0)
                                {
                                    using var reader = Windows.Storage.Streams.DataReader.FromBuffer(readResult.Value);
                                    byte batteryPercent = reader.ReadByte();
                                    return Math.Clamp((int)batteryPercent, 0, 100);
                                }
                            }
                        }
                    }
                }
                catch { }
                return -1;
            }, cts.Token);
        }
        catch { }
        return -1;
    }

    private static bool TryExtractBatteryLevel(DeviceInformation di, out int level)
    {
        level = -1;
        if (di.Properties.TryGetValue(PROP_BATTERY_PERCENTAGE, out var batVal) && batVal != null)
        {
            if (batVal is byte b) level = b;
            else if (batVal is int i) level = i;
            else if (int.TryParse(batVal.ToString(), out int parsed)) level = parsed;

            if (level >= 0 && level <= 100) return true;
        }
        return false;
    }

    private static string GetDeviceName(DeviceInformation di, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(di.Name)) return di.Name;
        if (di.Properties.TryGetValue(PROP_ITEM_NAME_DISPLAY, out var dispName) && dispName != null)
        {
            string? str = dispName.ToString();
            if (!string.IsNullOrWhiteSpace(str)) return str;
        }
        return fallback;
    }

    private static bool IsConnected(DeviceInformation di)
    {
        if (di.Properties.TryGetValue(PROP_AEP_IS_CONNECTED, out var connVal) && connVal != null)
        {
            if (connVal is bool b) return b;
            if (bool.TryParse(connVal.ToString(), out bool parsed)) return parsed;
        }
        return true;
    }

    private static DeviceBatteryInfo CreateDeviceInfo(string id, string name, int level, OmniGlanceSettings settings, DeviceInformation di)
    {
        var cat = GuessCategory(name);
        return new DeviceBatteryInfo
        {
            Id = id,
            Name = name,
            Category = cat,
            BatteryLevel = level,
            IsCharging = false,
            IsConnected = IsConnected(di),
            IsLowBattery = level <= settings.LowBatteryThreshold && level > settings.CriticalBatteryThreshold,
            IsCriticalBattery = level <= settings.CriticalBatteryThreshold
        };
    }

    private static DeviceCategory GuessCategory(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DeviceCategory.Generic;
        string lower = name.ToLowerInvariant();

        // 左右個別TWSイヤホンの判定
        bool isLeft = lower.Contains("(l)") || lower.Contains("[l]") || lower.EndsWith("-l") || lower.EndsWith("_l") ||
                     lower.Contains(" left") || lower.Contains(" 左") || lower.Contains("(left)") || lower.Contains("[left]");
        bool isRight = lower.Contains("(r)") || lower.Contains("[r]") || lower.EndsWith("-r") || lower.EndsWith("_r") ||
                      lower.Contains(" right") || lower.Contains(" 右") || lower.Contains("(right)") || lower.Contains("[right]");

        if (isLeft && !isRight) return DeviceCategory.EarbudLeft;
        if (isRight && !isLeft) return DeviceCategory.EarbudRight;

        // 完全ワイヤレスイヤホン
        if (lower.Contains("earbud") || lower.Contains("buds") || lower.Contains("tws") || lower.Contains("earphone") ||
            lower.Contains("airpods") || lower.Contains("wf-") || lower.Contains("linkbuds") || lower.Contains("freebuds") ||
            lower.Contains("ear") || lower.Contains("pod"))
        {
            return DeviceCategory.Earbuds;
        }

        // ヘッドホン・ヘッドセット
        if (lower.Contains("headphone") || lower.Contains("wh-") || lower.Contains("qc35") || lower.Contains("qc45") ||
            lower.Contains("quietcomfort") || lower.Contains("momentum") || lower.Contains("headset") || lower.Contains("head") || lower.Contains("audio"))
        {
            return DeviceCategory.Headphones;
        }

        // マウス
        if (lower.Contains("mouse")) return DeviceCategory.Mouse;

        // キーボード
        if (lower.Contains("key") || lower.Contains("board")) return DeviceCategory.Keyboard;

        // ゲームコントローラー
        if (lower.Contains("game") || lower.Contains("controller") || lower.Contains("xbox") || lower.Contains("pad") || lower.Contains("dualsense"))
        {
            return DeviceCategory.Controller;
        }

        // ペン
        if (lower.Contains("pen") || lower.Contains("stylus")) return DeviceCategory.Pen;

        return DeviceCategory.Generic;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (_aepWatcher != null)
                {
                    _aepWatcher.Added -= OnDeviceWatcherEvent;
                    _aepWatcher.Updated -= OnDeviceWatcherEvent;
                    _aepWatcher.Removed -= OnDeviceWatcherEvent;
                    _aepWatcher.Stop();
                    _aepWatcher = null;
                }
            }
            catch { }

            try
            {
                if (_batWatcher != null)
                {
                    _batWatcher.Added -= OnDeviceWatcherEvent;
                    _batWatcher.Updated -= OnDeviceWatcherEvent;
                    _batWatcher.Removed -= OnDeviceWatcherEvent;
                    _batWatcher.Stop();
                    _batWatcher = null;
                }
            }
            catch { }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
