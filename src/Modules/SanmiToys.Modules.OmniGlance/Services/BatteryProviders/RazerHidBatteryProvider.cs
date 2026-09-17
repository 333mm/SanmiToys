using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using Windows.Devices.Enumeration;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class RazerHidBatteryProvider : IBatteryProvider
{
    private const ushort RAZER_VID = 0x1532;
    private const string PROP_BATTERY_PERCENTAGE = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public string ProviderName => "RazerHid";

    #region Win32 P/Invoke Definitions

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool HidD_GetProductString(IntPtr HidDeviceObject, StringBuilder Buffer, int BufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    #endregion

    public async Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. ネイティブ HID 直接クエリ (OpenRazer 互換 Razer レポートプロトコル)
        try
        {
            var hidDevices = await QueryRazerNativeHidAsync(settings);
            foreach (var d in hidDevices)
            {
                if (seenNames.Add(d.Name))
                {
                    result.Add(d);
                }
            }
        }
        catch { }

        // 2. Windows DeviceInformation の PnP プロパティバッグからのクエリ
        try
        {
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

                            if (seenNames.Add(name))
                            {
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
                }
                catch { }
            }
        }
        catch { }

        // 3. Razer Synapse 3 / Synapse 4 ローカルログ/キャッシュからのフォールバック検出
        if (result.Count == 0)
        {
            try
            {
                var synDevice = await QuerySynapseLogsAsync(settings);
                if (synDevice != null && seenNames.Add(synDevice.Name))
                {
                    result.Add(synDevice);
                }
            }
            catch { }
        }

        return result;
    }

    private static async Task<List<DeviceBatteryInfo>> QueryRazerNativeHidAsync(OmniGlanceSettings settings)
    {
        var list = new List<DeviceBatteryInfo>();

        await Task.Run(() =>
        {
            try
            {
                string aqsFilter = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\"";
                var devs = DeviceInformation.FindAllAsync(aqsFilter).AsTask().GetAwaiter().GetResult();

                foreach (var di in devs)
                {
                    if (!di.Id.Contains("VID_1532", StringComparison.OrdinalIgnoreCase)) continue;

                    IntPtr hDev = CreateFile(
                        di.Id,
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        0,
                        IntPtr.Zero);

                    if (hDev == INVALID_HANDLE_VALUE)
                    {
                        hDev = CreateFile(
                            di.Id,
                            0,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero,
                            OPEN_EXISTING,
                            0,
                            IntPtr.Zero);
                    }

                    if (hDev == INVALID_HANDLE_VALUE) continue;

                    try
                    {
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(hDev, ref attr) || attr.VendorID != RAZER_VID)
                        {
                            continue;
                        }

                        // Razer Battery Request (91 bytes Feature Report: 0x00 ReportID + 90 bytes razer_report)
                        byte[] req = CreateRazerReport(0x07, 0x80);
                        if (HidD_SetFeature(hDev, req, req.Length))
                        {
                            byte[] resp = new byte[91];
                            if (HidD_GetFeature(hDev, resp, resp.Length))
                            {
                                if (resp[1] == 0x02 && resp[7] == 0x07 && resp[8] == 0x80)
                                {
                                    byte rawBat = resp[10];
                                    if (rawBat == 0 && resp[9] > 0 && resp[9] <= 255)
                                    {
                                        rawBat = resp[9];
                                    }

                                    int pct = (int)Math.Round((float)rawBat / 255.0f * 100.0f);
                                    if (pct > 100) pct = 100;

                                    if (pct >= 0)
                                    {
                                        var sb = new StringBuilder(128);
                                        string devName = "Razer Wireless Mouse";
                                        if (HidD_GetProductString(hDev, sb, sb.Capacity) && sb.Length > 0)
                                        {
                                            devName = sb.ToString();
                                        }

                                        bool isCharging = false;
                                        try
                                        {
                                            byte[] chargeReq = CreateRazerReport(0x07, 0x84);
                                            if (HidD_SetFeature(hDev, chargeReq, chargeReq.Length))
                                            {
                                                byte[] chargeResp = new byte[91];
                                                if (HidD_GetFeature(hDev, chargeResp, chargeResp.Length))
                                                {
                                                    if (chargeResp[1] == 0x02 && chargeResp[10] == 0x01)
                                                    {
                                                        isCharging = true;
                                                    }
                                                }
                                            }
                                        }
                                        catch { }

                                        var cat = GuessCategory(devName);
                                        list.Add(new DeviceBatteryInfo
                                        {
                                            Id = $"RAZER_{attr.ProductID:X4}_{di.Id}",
                                            Name = devName,
                                            Category = cat,
                                            BatteryLevel = pct,
                                            IsCharging = isCharging,
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
                    catch { }
                    finally
                    {
                        CloseHandle(hDev);
                    }
                }
            }
            catch { }
        });

        return list;
    }

    private static byte[] CreateRazerReport(byte commandClass, byte commandId, byte[]? args = null)
    {
        byte[] report = new byte[91];
        report[0] = 0x00;
        report[1] = 0x00;
        report[2] = 0x1F;
        report[3] = 0x00;
        report[4] = 0x00;
        report[5] = 0x00;
        report[6] = (byte)(args != null ? args.Length : 0x02);
        report[7] = commandClass;
        report[8] = commandId;

        if (args != null)
        {
            for (int i = 0; i < args.Length && i < 80; i++)
            {
                report[9 + i] = args[i];
            }
        }

        byte crc = 0;
        for (int i = 3; i <= 88; i++)
        {
            crc ^= report[i];
        }
        report[89] = crc;
        report[90] = 0x00;

        return report;
    }

    private static async Task<DeviceBatteryInfo?> QuerySynapseLogsAsync(OmniGlanceSettings settings)
    {
        string[] searchDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Razer", "Synapse3", "Log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Razer", "Synapse4", "Log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Razer", "Synapse3", "Log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Razer", "Synapse4", "Log"),
        };

        foreach (var synPath in searchDirs)
        {
            if (!Directory.Exists(synPath)) continue;

            var logFiles = Directory.GetFiles(synPath, "*device*.log")
                .Concat(Directory.GetFiles(synPath, "*battery*.log"))
                .OrderByDescending(f => File.GetLastWriteTime(f));

            foreach (var logFile in logFiles)
            {
                try
                {
                    using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    string? line;
                    while ((line = await sr.ReadLineAsync()) != null)
                    {
                        if (line.Contains("Battery", StringComparison.OrdinalIgnoreCase) && line.Contains("%"))
                        {
                            int pctIdx = line.IndexOf('%');
                            if (pctIdx > 0)
                            {
                                int numStart = pctIdx - 1;
                                while (numStart >= 0 && char.IsDigit(line[numStart])) numStart--;
                                string numStr = line.Substring(numStart + 1, pctIdx - numStart - 1).Trim();
                                if (int.TryParse(numStr, out int pct) && pct >= 0 && pct <= 100)
                                {
                                    return new DeviceBatteryInfo
                                    {
                                        Id = "RAZER_SYNAPSE_ACTIVE",
                                        Name = "Razer Wireless Mouse",
                                        Category = DeviceCategory.Mouse,
                                        BatteryLevel = pct,
                                        IsCharging = false,
                                        IsConnected = true,
                                        IsLowBattery = pct <= settings.LowBatteryThreshold && pct > settings.CriticalBatteryThreshold,
                                        IsCriticalBattery = pct <= settings.CriticalBatteryThreshold
                                    };
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        return null;
    }

    private static DeviceCategory GuessCategory(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("keyboard") || lower.Contains("blackwidow") || lower.Contains("huntsman") || lower.Contains("deathstalker")) return DeviceCategory.Keyboard;
        if (lower.Contains("headset") || lower.Contains("blackshark") || lower.Contains("barracuda") || lower.Contains("kraken")) return DeviceCategory.Headset;
        return DeviceCategory.Mouse; // Viper, DeathAdder, Basilisk, Cobra 等
    }
}
