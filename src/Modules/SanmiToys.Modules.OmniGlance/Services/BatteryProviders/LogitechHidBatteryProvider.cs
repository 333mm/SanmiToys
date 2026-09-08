using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using Windows.Devices.Enumeration;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class LogitechHidBatteryProvider : IBatteryProvider
{
    private const ushort LOGITECH_VID = 0x046D;
    private const string PROP_BATTERY_PERCENTAGE = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public string ProviderName => "LogitechHid";

    public async Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();

        // 1. 最優先: G HUB リアルタイム WebSocket API (ws://localhost:9010)
        // G HUB が稼働していれば、数ミリ秒で最も正確なリアルタイムバッテリー残量・充電状態が取得可能
        try
        {
            var wsDevices = await QueryGHubWebSocketAsync(settings);
            if (wsDevices.Count > 0)
            {
                return wsDevices;
            }
        }
        catch { }

        // 2. フォールバック: Windows DeviceInformation プロパティバッグからのクエリ
        try
        {
            string[] requestedProperties = new[]
            {
                PROP_BATTERY_PERCENTAGE,
                "System.ItemNameDisplay",
                "System.Devices.Aep.IsConnected",
                "System.Devices.DeviceManufacturer"
            };

            string aqsFilter = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\""; // HID GUID
            var devices = await DeviceInformation.FindAllAsync(aqsFilter, requestedProperties).AsTask();

            foreach (var dev in devices)
            {
                try
                {
                    string idUpper = dev.Id.ToUpperInvariant();
                    if (!idUpper.Contains("VID_046D") && !idUpper.Contains("LOGITECH"))
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
                                name = dispName.ToString() ?? "Logitech Device";
                            }
                            if (string.IsNullOrWhiteSpace(name)) name = "Logitech Wireless Device";

                            if (result.Any(r => r.Name == name)) continue;

                            var cat = GuessCategory(name);
                            result.Add(new DeviceBatteryInfo
                            {
                                Id = $"LOGI_{dev.Id}",
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

    /// <summary>
    /// G HUB のローカル WebSocket (ws://localhost:9010) に問い合わせて
    /// リアルタイムのデバイス一覧と正確なバッテリー残量・充電状態を取得する
    /// </summary>
    private static async Task<List<DeviceBatteryInfo>> QueryGHubWebSocketAsync(OmniGlanceSettings settings)
    {
        var devices = new List<DeviceBatteryInfo>();

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "file://");
        ws.Options.AddSubProtocol("json");

        using var cts = new CancellationTokenSource(1200); // 1.2秒タイムアウト

        try
        {
            await ws.ConnectAsync(new Uri("ws://localhost:9010"), cts.Token);
            if (ws.State != WebSocketState.Open) return devices;

            // 1. デバイス一覧を要求
            await SendWsMessageAsync(ws, "{\"verb\":\"GET\",\"path\":\"/devices/list\"}", cts.Token);

            var listMsg = await ReceiveWsMessageMatchingAsync(ws, m => m.Contains("/devices/list"), cts.Token);
            if (string.IsNullOrEmpty(listMsg)) return devices;

            using var listDoc = JsonDocument.Parse(listMsg);
            if (!listDoc.RootElement.TryGetProperty("payload", out var payloadElem) ||
                !payloadElem.TryGetProperty("deviceInfos", out var infosElem))
            {
                return devices;
            }

            foreach (var item in infosElem.EnumerateArray())
            {
                string devId = item.TryGetProperty("id", out var idProp) ? (idProp.GetString() ?? "") : "";
                string displayName = item.TryGetProperty("displayName", out var dnProp) ? (dnProp.GetString() ?? "") : "";
                string devType = item.TryGetProperty("deviceType", out var dtProp) ? (dtProp.GetString() ?? "") : "";
                string state = item.TryGetProperty("state", out var stProp) ? (stProp.GetString() ?? "") : "";

                if (string.IsNullOrEmpty(devId) || state.Equals("INACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 2. 対象デバイスのバッテリーステータスを要求
                await SendWsMessageAsync(ws, $"{{\"verb\":\"GET\",\"path\":\"/battery/{devId}/state\"}}", cts.Token);
                var batMsg = await ReceiveWsMessageMatchingAsync(ws, m => m.Contains($"/battery/{devId}/state"), cts.Token);

                if (string.IsNullOrEmpty(batMsg)) continue;

                using var batDoc = JsonDocument.Parse(batMsg);
                if (batDoc.RootElement.TryGetProperty("payload", out var batPayload))
                {
                    if (batPayload.TryGetProperty("percentage", out var pctElem) && pctElem.TryGetInt32(out int pct))
                    {
                        pct = Math.Clamp(pct, 0, 100);
                        bool isCharging = batPayload.TryGetProperty("charging", out var chProp) && chProp.GetBoolean();

                        string name = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Logitech Device";
                        var cat = devType.Equals("MOUSE", StringComparison.OrdinalIgnoreCase)
                            ? DeviceCategory.Mouse
                            : GuessCategory(name);

                        devices.Add(new DeviceBatteryInfo
                        {
                            Id = $"LOGI_{devId}",
                            Name = name,
                            Category = cat,
                            BatteryLevel = pct,
                            IsCharging = isCharging,
                            IsConnected = true,
                            IsLowBattery = !isCharging && pct <= settings.LowBatteryThreshold && pct > settings.CriticalBatteryThreshold,
                            IsCriticalBattery = !isCharging && pct <= settings.CriticalBatteryThreshold
                        });
                    }
                }
            }
        }
        catch { }

        return devices;
    }

    private static async Task SendWsMessageAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveWsMessageMatchingAsync(ClientWebSocket ws, Func<string, bool> predicate, CancellationToken ct)
    {
        var buffer = new byte[65536];
        for (int i = 0; i < 8; i++) // 最大8メッセージまで巡回
        {
            if (ws.State != WebSocketState.Open || ct.IsCancellationRequested) break;

            var seg = new ArraySegment<byte>(buffer);
            var result = await ws.ReceiveAsync(seg, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;

            string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (predicate(text))
            {
                return text;
            }
        }
        return null;
    }

    private static DeviceCategory GuessCategory(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("keyboard") || lower.Contains("g913") || lower.Contains("g915") || lower.Contains("craft")) return DeviceCategory.Keyboard;
        if (lower.Contains("headset") || lower.Contains("pro x") || lower.Contains("g733") || lower.Contains("g435") || lower.Contains("g935") || lower.Contains("yeti")) return DeviceCategory.Headset;
        return DeviceCategory.Mouse; // G PRO, G502, MX Master 等
    }
}
