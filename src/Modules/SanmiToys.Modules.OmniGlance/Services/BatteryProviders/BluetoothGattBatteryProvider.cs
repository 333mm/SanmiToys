using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class BluetoothGattBatteryProvider : IBatteryProvider
{
    public string ProviderName => "BluetoothGatt";

    public async Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask();

            foreach (var di in devices)
            {
                try
                {
                    using var ble = await BluetoothLEDevice.FromIdAsync(di.Id).AsTask();
                    if (ble == null || ble.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    {
                        continue;
                    }

                    var gattServices = await ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery).AsTask();
                    if (gattServices.Status == GattCommunicationStatus.Success)
                    {
                        foreach (var s in gattServices.Services)
                        {
                            var characteristics = await s.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel).AsTask();
                            if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0)
                            {
                                var readResult = await characteristics.Characteristics[0].ReadValueAsync().AsTask();
                                if (readResult.Status == GattCommunicationStatus.Success && readResult.Value != null && readResult.Value.Length > 0)
                                {
                                    using var reader = Windows.Storage.Streams.DataReader.FromBuffer(readResult.Value);
                                    byte batteryPercent = reader.ReadByte();

                                    var cat = GuessCategory(ble.Name);
                                    int level = Math.Clamp((int)batteryPercent, 0, 100);

                                    result.Add(new DeviceBatteryInfo
                                    {
                                        Id = $"BLE_{di.Id}",
                                        Name = string.IsNullOrWhiteSpace(ble.Name) ? "Wireless Device" : ble.Name,
                                        Category = cat,
                                        BatteryLevel = level,
                                        IsCharging = false,
                                        IsConnected = true,
                                        IsLowBattery = level <= settings.LowBatteryThreshold && level > settings.CriticalBatteryThreshold,
                                        IsCriticalBattery = level <= settings.CriticalBatteryThreshold
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return result;
    }

    private static DeviceCategory GuessCategory(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DeviceCategory.Generic;
        string lower = name.ToLowerInvariant();
        if (lower.Contains("mouse")) return DeviceCategory.Mouse;
        if (lower.Contains("key") || lower.Contains("board")) return DeviceCategory.Keyboard;
        if (lower.Contains("head") || lower.Contains("ear") || lower.Contains("pod") || lower.Contains("buds") || lower.Contains("audio")) return DeviceCategory.Headset;
        if (lower.Contains("game") || lower.Contains("controller") || lower.Contains("xbox") || lower.Contains("pad")) return DeviceCategory.Controller;
        if (lower.Contains("pen") || lower.Contains("stylus")) return DeviceCategory.Pen;
        return DeviceCategory.Generic;
    }
}
