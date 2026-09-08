using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

/// <summary>
/// Windows の標準「スマートフォン連携 (Phone Link / Microsoft.YourPhone)」から
/// スマートフォンの接続状態およびバッテリー情報を取得するプロバイダー。
/// </summary>
public class PhoneLinkBatteryProvider : IBatteryProvider
{
    public string ProviderName => "PhoneLink";

    public Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();

        try
        {
            var packageDir = FindYourPhonePackageDirectory();
            if (string.IsNullOrEmpty(packageDir) || !Directory.Exists(packageDir))
            {
                return Task.FromResult(result);
            }

            string companionPath = Path.Combine(packageDir, "LocalState", "StartMenu", "StartMenuCompanion.json");
            string metaPath = Path.Combine(packageDir, "LocalCache", "DeviceMetadataStorage.json");

            if (!File.Exists(companionPath))
            {
                return Task.FromResult(result);
            }

            // 1. メタデータからデバイス名・ID・連携状態を取得
            string deviceName = "スマートフォン";
            string deviceId = "phonelink_phone";
            bool isLinked = true; // companion が存在していれば基本的に連携設定済み

            if (File.Exists(metaPath))
            {
                try
                {
                    string metaJsonStr = ReadFileShared(metaPath);
                    using var metaDoc = JsonDocument.Parse(metaJsonStr);
                    if (metaDoc.RootElement.TryGetProperty("DeviceMetadatas", out var devMetas))
                    {
                        foreach (var userProp in devMetas.EnumerateObject())
                        {
                            if (userProp.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var devElem in userProp.Value.EnumerateArray())
                                {
                                    bool linked = devElem.TryGetProperty("IsLinked", out var lp) && lp.GetBoolean();
                                    if (devElem.TryGetProperty("Metadata", out var metaElem))
                                    {
                                        if (metaElem.TryGetProperty("Id", out var idElem))
                                        {
                                            deviceId = "phonelink_" + idElem.GetString();
                                        }

                                        if (metaElem.TryGetProperty("Metadata", out var innerMeta))
                                        {
                                            if (innerMeta.TryGetProperty("DisplayName", out var nameElem))
                                            {
                                                string? dn = nameElem.GetString();
                                                if (!string.IsNullOrWhiteSpace(dn))
                                                {
                                                    deviceName = dn;
                                                }
                                            }
                                        }
                                    }

                                    if (linked)
                                    {
                                        isLinked = true;
                                        break;
                                    }
                                }
                            }
                            if (isLinked && deviceName != "スマートフォン") break;
                        }
                    }
                }
                catch { }
            }

            if (!isLinked)
            {
                return Task.FromResult(result);
            }

            // 2. StartMenuCompanion.json をパースしてバッテリー残量・充電状態・接続状態を取得
            string compJsonStr = ReadFileShared(companionPath);
            if (string.IsNullOrWhiteSpace(compJsonStr))
            {
                return Task.FromResult(result);
            }

            int batteryLevel = -1;
            bool isCharging = false;
            bool isConnected = true;

            // JSON 内を再帰的に走査してバッテリーテキストを探す
            using (var compDoc = JsonDocument.Parse(compJsonStr))
            {
                // speak プロパティからの名前補強
                if (deviceName == "スマートフォン" && compDoc.RootElement.TryGetProperty("speak", out var speakElem))
                {
                    string? speak = speakElem.GetString();
                    if (!string.IsNullOrWhiteSpace(speak))
                    {
                        var parts = speak.Split(new[] { " - ", " – ", " — " }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                        {
                            deviceName = parts[0].Trim();
                        }
                    }
                }

                ExtractBatteryInfoFromElement(compDoc.RootElement, ref batteryLevel, ref isCharging, ref isConnected);
            }

            // 見つからなかった場合の正規表現フォールバック
            if (batteryLevel < 0)
            {
                var match = Regex.Match(compJsonStr, @"(?i)(?:バッテリー|バッテリ|battery)[^0-9%]{0,20}(\d{1,3})%");
                if (!match.Success)
                {
                    match = Regex.Match(compJsonStr, @"(\d{1,3})%");
                }

                if (match.Success && int.TryParse(match.Groups[1].Value, out int pct))
                {
                    batteryLevel = Math.Clamp(pct, 0, 100);
                }
            }

            if (batteryLevel >= 0)
            {
                // 充電文字列チェック
                if (!isCharging)
                {
                    string lower = compJsonStr.ToLowerInvariant();
                    if (lower.Contains("充電中") || lower.Contains("充電完了") || lower.Contains("charging") || lower.Contains("charged"))
                    {
                        isCharging = true;
                    }
                }

                result.Add(new DeviceBatteryInfo
                {
                    Id = deviceId,
                    Name = deviceName,
                    Category = DeviceCategory.Phone,
                    BatteryLevel = batteryLevel,
                    IsCharging = isCharging,
                    IsConnected = isConnected,
                    IsLowBattery = !isCharging && batteryLevel <= settings.LowBatteryThreshold && batteryLevel > settings.CriticalBatteryThreshold,
                    IsCriticalBattery = !isCharging && batteryLevel <= settings.CriticalBatteryThreshold
                });
            }
        }
        catch { }

        return Task.FromResult(result);
    }

    private static void ExtractBatteryInfoFromElement(JsonElement element, ref int batteryLevel, ref bool isCharging, ref bool isConnected)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    string pName = prop.Name.ToLowerInvariant();
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string strVal = prop.Value.GetString() ?? "";

                        // バッテリー残量の抽出
                        if (pName is "text" or "tooltip" or "alttext")
                        {
                            var m = Regex.Match(strVal, @"(?i)(?:残量|battery)?[^0-9%]{0,10}(\d{1,3})%");
                            if (m.Success && int.TryParse(m.Groups[1].Value, out int b))
                            {
                                batteryLevel = Math.Clamp(b, 0, 100);
                            }

                            string lower = strVal.ToLowerInvariant();
                            if (lower.Contains("充電") || lower.Contains("charging"))
                            {
                                isCharging = true;
                            }

                            if (lower.Contains("切断") || lower.Contains("disconnected") || lower.Contains("オフライン"))
                            {
                                isConnected = false;
                            }
                        }
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        ExtractBatteryInfoFromElement(prop.Value, ref batteryLevel, ref isCharging, ref isConnected);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ExtractBatteryInfoFromElement(item, ref batteryLevel, ref isCharging, ref isConnected);
                }
                break;
        }
    }

    private static string? FindYourPhonePackageDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string packagesRoot = Path.Combine(localAppData, "Packages");

        if (!Directory.Exists(packagesRoot)) return null;

        // 1. 代表的なパッケージフォルダ名
        string standardPath = Path.Combine(packagesRoot, "Microsoft.YourPhone_8wekyb3d8bbwe");
        if (Directory.Exists(standardPath))
        {
            return standardPath;
        }

        // 2. 見つからない場合はワイルドカード検索
        try
        {
            var matches = Directory.GetDirectories(packagesRoot, "*YourPhone*");
            foreach (var match in matches)
            {
                if (File.Exists(Path.Combine(match, "LocalState", "StartMenu", "StartMenuCompanion.json")))
                {
                    return match;
                }
            }

            if (matches.Length > 0) return matches[0];
        }
        catch { }

        return null;
    }

    private static string ReadFileShared(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
