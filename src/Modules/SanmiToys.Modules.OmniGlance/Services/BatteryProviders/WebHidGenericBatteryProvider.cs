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

    // WebHID / 各種ワイヤレスゲーミング・オーディオ・キーボード機器の代表的VID
    private static readonly HashSet<string> KnownWebHidVids = new(StringComparer.OrdinalIgnoreCase)
    {
        "VID_1038", // SteelSeries
        "VID_1B1C", // Corsair
        "VID_0B05", // ASUS ROG
        "VID_03F0", // HP / HyperX
        "VID_0951", // Kingston / HyperX
        "VID_054C", // Sony
        "VID_05AC", // Apple / Beats
        "VID_05A7", // Bose
        "VID_1395", // Sennheiser / EPOS
        "VID_0909", // Audio-Technica (オーディオテクニカ)
        "VID_291A", // Anker / Soundcore
        "VID_1E7D", // Roccat / Turtle Beach
        "VID_10F5", // Turtle Beach
        "VID_22D4", // Glorious
        "VID_31E3", // Wooting
        "VID_1532", // Razer
        "VID_046D", // Logitech / Logicool
        "VID_361D", // Finalmouse
        "VID_3434", // Keychron
        "VID_2DC8", // 8BitDo
        "VID_045E", // Microsoft
        "VID_258A", // SinoWealth (Lamzu, Pulsar, Darmoshark, VGN/VXE)
        "VID_3554", // ATK / VXE Hub
        "VID_24AE", // Rapoo / Generic Wireless
        "VID_3151", // Yowkeox / Epomaker / Generic Gaming
        "VID_1915", // Nordic Semiconductor Wireless Dongle
        "VID_0483"  // STMicroelectronics (Custom WebHID / Supreme)
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

                    // バッテリープロパティを確認（既知VID、またはOSがバッテリー残量を通知しているHIDデバイス全般）
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
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                name = isKnown ? "Wireless Gaming Device" : "Wireless Device";
                            }

                            // 既知VIDでなく名前も取得できない汎用デバイスは誤検知防止のため除外
                            if (!isKnown && (string.IsNullOrWhiteSpace(name) || name == "Wireless Device"))
                            {
                                continue;
                            }

                            var cat = GuessCategory(name);
                            string uniqueKey = $"{cat}_{dev.Id}_{name}".ToLowerInvariant();
                            if (result.Any(r => $"{r.Category}_{r.Id}_{r.Name}".ToLowerInvariant() == uniqueKey))
                            {
                                continue;
                            }

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
        if (string.IsNullOrWhiteSpace(name)) return DeviceCategory.Mouse;
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
            lower.Contains("airpods") || lower.Contains("wf-") || lower.Contains("linkbuds") || lower.Contains("freebuds"))
        {
            return DeviceCategory.Earbuds;
        }

        // ヘッドホン・ヘッドセット
        if (lower.Contains("headphone") || lower.Contains("wh-") || lower.Contains("qc35") || lower.Contains("qc45") ||
            lower.Contains("quietcomfort") || lower.Contains("momentum") || lower.Contains("headset") || lower.Contains("audio"))
        {
            return DeviceCategory.Headphones;
        }

        // キーボード
        if (lower.Contains("keyboard") || lower.Contains("keychron") || lower.Contains("atk75") || lower.Contains("wooting") ||
            lower.Contains("g913") || lower.Contains("blackwidow") || lower.Contains("apex") || lower.Contains("k70") ||
            lower.Contains("k100") || lower.Contains("board"))
        {
            return DeviceCategory.Keyboard;
        }

        // コントローラー
        if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("8bitdo") || lower.Contains("xbox"))
        {
            return DeviceCategory.Controller;
        }

        return DeviceCategory.Mouse; // Supreme, Lamzu, Pulsar, ATK, VXE, SteelSeries, Corsair等のマウス
    }
}
