using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using Windows.Devices.Enumeration;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class RazerHidBatteryProvider : IBatteryProvider
{
    private const ushort RAZER_VID = 0x1532;
    private const string PROP_BATTERY_PERCENTAGE = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public string ProviderName => "RazerHid";

    public async Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();

        try
        {
            // 1. Windows DeviceInformation のプロパティバッグから Razer デバイスのバッテリーをクエリ
            string[] requestedProperties = new[]
            {
                PROP_BATTERY_PERCENTAGE,
                "System.ItemNameDisplay",
                "System.Devices.Aep.IsConnected"
            };

            string aqsFilter = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\"";
            var devices = await DeviceInformation.FindAllAsync(aqsFilter, requestedProperties).AsTask();

            foreach (var dev in devices)
            {
                try
                {
                    string idUpper = dev.Id.ToUpperInvariant();
                    if (!idUpper.Contains("VID_1532") && !idUpper.Contains("RAZER"))
                    {
                        continue;
                    }

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
                                name = dispName.ToString() ?? "Razer Device";
                            }
                            if (string.IsNullOrWhiteSpace(name)) name = "Razer Wireless Device";

                            if (result.Any(r => r.Name == name)) continue;

                            var cat = GuessCategory(name);
                            result.Add(new DeviceBatteryInfo
                            {
                                Id = $"RAZER_{dev.Id}",
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

        // 2. Razer Synapse 3 ローカルログ/キャッシュからのフォールバック検出
        if (result.Count == 0)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string synPath = Path.Combine(localAppData, "Razer", "Synapse3", "Log");
                if (Directory.Exists(synPath))
                {
                    var logFile = Directory.GetFiles(synPath, "*device*.log")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .FirstOrDefault();

                    if (logFile != null)
                    {
                        using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        string? line;
                        while ((line = await sr.ReadLineAsync()) != null)
                        {
                            if (line.Contains("Battery", StringComparison.OrdinalIgnoreCase) && line.Contains("%"))
                            {
                                // "Battery: 85%" などを抽出
                                int pctIdx = line.IndexOf('%');
                                if (pctIdx > 0)
                                {
                                    int numStart = pctIdx - 1;
                                    while (numStart >= 0 && char.IsDigit(line[numStart])) numStart--;
                                    string numStr = line.Substring(numStart + 1, pctIdx - numStart - 1).Trim();
                                    if (int.TryParse(numStr, out int pct) && pct >= 0 && pct <= 100)
                                    {
                                        result.Add(new DeviceBatteryInfo
                                        {
                                            Id = "RAZER_SYNAPSE_ACTIVE",
                                            Name = "Razer Wireless Mouse",
                                            Category = DeviceCategory.Mouse,
                                            BatteryLevel = pct,
                                            IsCharging = false,
                                            IsConnected = true,
                                            IsLowBattery = pct <= settings.LowBatteryThreshold && pct > settings.CriticalBatteryThreshold,
                                            IsCriticalBattery = pct <= settings.CriticalBatteryThreshold
                                        });
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private static DeviceCategory GuessCategory(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("keyboard") || lower.Contains("blackwidow") || lower.Contains("huntsman") || lower.Contains("deathstalker")) return DeviceCategory.Keyboard;
        if (lower.Contains("headset") || lower.Contains("blackshark") || lower.Contains("barracuda") || lower.Contains("kraken")) return DeviceCategory.Headset;
        return DeviceCategory.Mouse; // Viper, DeathAdder, Basilisk, Cobra 等
    }
}
