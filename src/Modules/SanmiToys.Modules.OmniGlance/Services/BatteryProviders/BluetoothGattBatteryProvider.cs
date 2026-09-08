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
}
