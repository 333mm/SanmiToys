using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using Windows.Devices.Enumeration;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class WebHidGenericBatteryProvider : IBatteryProvider
{
    private const string PROP_BATTERY_PERCENTAGE = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    // WebHID系マウスで広く使われている代表的コントローラー/ブランドVID
    private static readonly HashSet<string> KnownWebHidVids = new(StringComparer.OrdinalIgnoreCase)
    {
        "VID_258A", // SinoWealth (Lamzu, Pulsar, Supreme, Darmoshark, VGN/VXE)
        "VID_3554", // ATK / VXE Hub
        "VID_24AE", // Rapoo / Generic Wireless
        "VID_3434", // Keychron Launcher WebHID
        "VID_3151", // Yowkeox / Generic Gaming
        "VID_1915", // Nordic Semiconductor Wireless Dongle
        "VID_0483", // STMicroelectronics (Custom WebHID / Supreme)
        "VID_1B1C", // Corsair
        "VID_1038"  // SteelSeries
    };

    public string ProviderName => "WebHidGeneric";

    public async Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();

        try
        {
            string[] requestedProperties = new[]
            {
                PROP_BATTERY_PERCENTAGE,
                "System.ItemNameDisplay",
                "System.Devices.Aep.IsConnected",
                "System.Devices.DeviceManufacturer"
            };

            string aqsFilter = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\"";
            var devices = await DeviceInformation.FindAllAsync(aqsFilter, requestedProperties).AsTask();

            foreach (var dev in devices)
            {
                try
                {
                    string idUpper = dev.Id.ToUpperInvariant();
                    bool isKnown = KnownWebHidVids.Any(vid => idUpper.Contains(vid));

                    if (!isKnown) continue;

                    if (dev.Properties.TryGetValue(PROP_BATTERY_PERCENTAGE, out var batVal) && batVal != null)
                    {
                        int level = -1;
                        if (batVal is byte b) level = b;
                        else if (batVal is int i) level = i;
                        else if (int.TryParse(batVal.ToString(), out int parsed)) level = parsed;

                        if (level >= 0 && level <= 100)
                        {
                            string name = dev.Name;
                            if (string.IsNullOrWhiteSpace(name) && dev.Properties.TryGetValue("System.ItemNameDisplay", out var dispName) && dispName != null)
                            {
                                name = dispName.ToString() ?? "Wireless Device";
                            }
                            if (string.IsNullOrWhiteSpace(name)) name = "Gaming Wireless Mouse";

                            if (result.Any(r => r.Name == name)) continue;

                            var cat = GuessCategory(name);
                            result.Add(new DeviceBatteryInfo
                            {
                                Id = $"WEBHID_{dev.Id}",
                                Name = name,
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
                catch { }
            }
        }
        catch { }

        return result;
    }

    private static DeviceCategory GuessCategory(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("keyboard") || lower.Contains("keychron") || lower.Contains("atk75") || lower.Contains("wooting")) return DeviceCategory.Keyboard;
        if (lower.Contains("headset") || lower.Contains("audio")) return DeviceCategory.Headset;
        return DeviceCategory.Mouse; // Supreme, Lamzu, Pulsar, ATK, VXE 等
    }
}
