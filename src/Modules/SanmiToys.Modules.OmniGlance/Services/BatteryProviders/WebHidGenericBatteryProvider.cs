using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;
using Windows.Devices.Enumeration;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

/// <summary>
/// Sprime、Ninjutso、ATK、VXE、VGN、Lamzu、Zaopin、Scyrox、Dareuなどの
/// WebHID/Webドライバ対応ワイヤレスゲーミングデバイスのネイティブHID直接クエリによるバッテリー検知プロバイダー。
/// </summary>
public class WebHidGenericBatteryProvider : IBatteryProvider
{
    private const string PROP_BATTERY_PERCENTAGE = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    #region Win32 P/Invoke Definitions

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid interfaceClassGuid;
        public int flags;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid HidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool HidD_GetProductString(IntPtr HidDeviceObject, StringBuilder Buffer, int BufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool HidD_GetManufacturerString(IntPtr HidDeviceObject, StringBuilder Buffer, int BufferLength);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, ref int RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;

    #endregion

    // 既知のモデル定義（VID, PID -> モデル名）
    private static readonly Dictionary<(ushort Vid, ushort Pid), string> KnownModels = new()
    {
        // Sprime PM1 (有線 / 1Kレシーバー / 4K・8Kレシーバー / ブートローダー)
        { (0x1915, 0xAC11), "Sprime PM1" },
        { (0x1915, 0xAC12), "Sprime PM1" },
        { (0x1915, 0xAC13), "Sprime PM1" },
        { (0x1915, 0xAC1C), "Sprime PM1" },
        { (0x1915, 0xAC8C), "Sprime PM1" },
        { (0x1915, 0xAC0B), "Sprime PM1" },
        // Ninjutso Sora
        { (0x1915, 0xAE1C), "Ninjutso Sora V2" },
        { (0x1915, 0xAE11), "Ninjutso Sora V2" },
        { (0x093A, 0xEB02), "Ninjutso Sora" },
        // ATK / VXE / VGN (Nordic 52840 / Compx)
        { (0x373B, 0x1031), "ATK F1 Ultimate" },
        { (0x373B, 0x102E), "ATK F1 Ultimate" },
        { (0x373B, 0x11D9), "ATK A9 Ultimate" },
        { (0x373B, 0x11B6), "ATK A9 Ultimate" },
        { (0x373B, 0x104D), "VXE MAD R" },
        { (0x373B, 0x103F), "VXE MAD R" },
        { (0x373B, 0x1040), "VXE MAD R Major Plus" },
        { (0x373B, 0x104C), "VXE MAD R Major Plus" },
        { (0x3554, 0xF58A), "VXE R1 Pro Max" },
        { (0x3554, 0xF58C), "VXE R1 Pro Max" },
        { (0x3554, 0xF58E), "VXE R1 SE+" },
        { (0x3554, 0xF58F), "VXE R1 SE+" },
        { (0x3554, 0xF503), "VGN F1 Pro" },
        { (0x3554, 0xF502), "VGN F1 Pro" },
        { (0x3554, 0xFB3E), "VGN F2 Pro Max" },
        { (0x3554, 0xFB3D), "VGN F2 Pro Max" },
        { (0x3554, 0xF524), "Zaopin Z2 Mini" },
        { (0x3554, 0xF526), "Zaopin Z2 Mini" },
        { (0x3554, 0xF5F7), "Scyrox V8" },
        { (0x3554, 0xF5F6), "Scyrox V8" },
        { (0x260D, 0x1175), "Dareu A950 Air" },
        { (0x260D, 0x1193), "Dareu A950 Air" },
        { (0x33E4, 0x3854), "G-Wolves Lycan" },
        { (0x33E4, 0x4719), "G-Wolves Lycan" },
        // ATK Zero (Nordic 54L15)
        { (0x373B, 0x1155), "ATK Zero" },
        { (0x373B, 0x1154), "ATK Zero" },
        { (0x373B, 0x124F), "ATK Zero" },
        // Lamzu
        { (0x373E, 0x001E), "Lamzu Maya X" },
        { (0x373E, 0x001C), "Lamzu Maya X" },
        { (0x37B0, 0x0010), "Lamzu Inca" },
        { (0x37B0, 0x0009), "Lamzu Inca" },
        // MCHOSE
        { (0x5253, 0x1020), "MCHOSE L7 Pro" },
        { (0x5253, 0x00B0), "MCHOSE L7 Pro" },
        // Attack Shark
        { (0x1D57, 0xFA60), "Attack Shark X3" },
        { (0x1D57, 0xFA61), "Attack Shark X3" },
    };

    // 直近のバッテリー残量キャッシュ (VID/PID -> (Battery, IsCharging))
    private static readonly Dictionary<uint, (int Battery, bool IsCharging)> _batteryCache = new();

    // WebHID / 各種ワイヤレスゲーミング機器の代表的VID
    private static readonly HashSet<string> KnownWebHidVids = new(StringComparer.OrdinalIgnoreCase)
    {
        "VID_1915", // Nordic Semiconductor (Sprime, Ninjutso, Compx)
        "VID_093A", // PixArt (Ninjutso)
        "VID_373B", // ATK / VXE
        "VID_3554", // ATK / VXE / VGN / Scyrox / Zaopin
        "VID_1A81", // Holtek (ATK keyboards/mice)
        "VID_258A", // SinoWealth (Lamzu, Pulsar, Darmoshark, VGN)
        "VID_373E", // Lamzu
        "VID_37B0", // Lamzu
        "VID_260D", // Dareu
        "VID_33E4", // G-Wolves
        "VID_5253", // Realtek (MCHOSE)
        "VID_1D57", // Attack Shark
        "VID_1038", // SteelSeries
        "VID_1B1C", // Corsair
        "VID_0B05", // ASUS ROG
        "VID_03F0", // HP / HyperX
        "VID_0951", // Kingston / HyperX
        "VID_054C", // Sony
        "VID_05AC", // Apple / Beats
        "VID_05A7", // Bose
        "VID_1395", // Sennheiser / EPOS
        "VID_0909", // Audio-Technica
        "VID_291A", // Anker / Soundcore
        "VID_1E7D", // Roccat / Turtle Beach
        "VID_10F5", // Turtle Beach
        "VID_22D4", // Glorious
        "VID_31E3", // Wooting
        "VID_361D", // Finalmouse
        "VID_3434", // Keychron
        "VID_2DC8", // 8BitDo
        "VID_24AE", // Rapoo
        "VID_3151"  // Yowkeox / Epomaker
    };

    public string ProviderName => "WebHidGeneric";

    public async Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();
        var scannedDevKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 1. ネイティブWin32 HID直接クエリ（Sprime / Ninjutso / ATK / Lamzu 等のWebHIDデバイス）
            var nativeDevs = await Task.Run(() => QueryNativeHidDevices(settings));
            foreach (var dev in nativeDevs)
            {
                string key = $"{dev.Category}_{dev.Id}_{dev.Name}".ToLowerInvariant();
                if (scannedDevKeys.Add(key))
                {
                    result.Add(dev);
                }
            }

            // 2. Windows PnP プロパティのフォールバック検知
            var pnpDevs = await QueryPnpDevicesAsync(settings);
            foreach (var dev in pnpDevs)
            {
                string key = $"{dev.Category}_{dev.Id}_{dev.Name}".ToLowerInvariant();
                if (scannedDevKeys.Add(key))
                {
                    result.Add(dev);
                }
            }
        }
        catch { }

        return result;
    }

    private List<DeviceBatteryInfo> QueryNativeHidDevices(OmniGlanceSettings settings)
    {
        var list = new List<DeviceBatteryInfo>();
        var queriedVidPids = new HashSet<uint>();

        Guid hidGuid;
        HidD_GetHidGuid(out hidGuid);
        IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hDevInfo == (IntPtr)(-1)) return list;

        try
        {
            SP_DEVICE_INTERFACE_DATA dia = new SP_DEVICE_INTERFACE_DATA();
            dia.cbSize = Marshal.SizeOf(dia);

            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index++, ref dia))
            {
                int reqSize = 0;
                SetupDiGetDeviceInterfaceDetail(hDevInfo, ref dia, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                if (reqSize == 0) continue;

                IntPtr detailData = Marshal.AllocHGlobal(reqSize);
                try
                {
                    Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 5);
                    if (!SetupDiGetDeviceInterfaceDetail(hDevInfo, ref dia, detailData, reqSize, ref reqSize, IntPtr.Zero))
                    {
                        continue;
                    }

                    IntPtr pPath = (IntPtr)((long)detailData + 4);
                    string? path = Marshal.PtrToStringAuto(pPath);
                    if (string.IsNullOrEmpty(path)) continue;

                    // デバイスハンドルのオープン（共有アクセス）
                    uint shareMode = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
                    IntPtr hDev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, shareMode, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (hDev == (IntPtr)(-1))
                    {
                        hDev = CreateFile(path, 0, shareMode, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    }
                    if (hDev == (IntPtr)(-1)) continue;

                    try
                    {
                        HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                        attr.Size = Marshal.SizeOf(attr);
                        if (!HidD_GetAttributes(hDev, ref attr)) continue;

                        ushort vid = attr.VendorID;
                        ushort pid = attr.ProductID;
                        uint vidPidKey = ((uint)vid << 16) | pid;

                        // ロジクール (0x046D) と Razer (0x1532) は専用プロバイダーがあるためスキップ
                        if (vid == 0x046D || vid == 0x1532) continue;

                        // 同一スキャン内で既にこのVID/PIDからバッテリーを取得済みの場合は重複クエリをスキップ
                        if (queriedVidPids.Contains(vidPidKey)) continue;

                        // コレクション情報の取得
                        IntPtr preparsed;
                        if (!HidD_GetPreparsedData(hDev, out preparsed)) continue;

                        HIDP_CAPS caps = new HIDP_CAPS();
                        try
                        {
                            HidP_GetCaps(preparsed, ref caps);
                        }
                        finally
                        {
                            HidD_FreePreparsedData(preparsed);
                        }

                        // Feature Report も Output Report も持たないコレクション（単なる標準マウスボタン入力等）はスキップ
                        if (caps.FeatureReportByteLength < 16 && caps.OutputReportByteLength < 16) continue;

                        int batteryLevel = -1;
                        bool isCharging = false;

                        // --- プロトコル A: Sprime / Ninjutso / Nordic Feature Report (Report ID 5 or 6) ---
                        if (caps.FeatureReportByteLength >= 16)
                        {
                            int reportLen = Math.Max(32, (int)caps.FeatureReportByteLength);
                            byte[] setBuf = new byte[reportLen];

                            // Sprime PM1 レシーバー (1K: 0xAC1C, 4K/8K: 0xAC8C) の場合、公式ドライバ同様に Opcode 34 (0x22) で無線リンクを初期化
                            if (vid == 0x1915 && (pid == 0xAC1C || pid == 0xAC8C))
                            {
                                try
                                {
                                    Array.Clear(setBuf, 0, setBuf.Length);
                                    setBuf[0] = 5;
                                    setBuf[1] = 34; // 0x22: Query Mouse Link / PID
                                    setBuf[4] = 1;
                                    setBuf[7] = 22; // 0x16
                                    if (HidD_SetFeature(hDev, setBuf, setBuf.Length))
                                    {
                                        System.Threading.Thread.Sleep(80);
                                        byte[] linkBuf = new byte[setBuf.Length];
                                        linkBuf[0] = 5;
                                        HidD_GetFeature(hDev, linkBuf, linkBuf.Length);
                                        System.Threading.Thread.Sleep(40);
                                    }
                                }
                                catch { }
                            }

                            byte[] candidateReportIds = new byte[] { 5, 6 };
                            byte[] profileBytes = new byte[] { 0, 22 };

                            foreach (byte rid in candidateReportIds)
                            {
                                foreach (byte prof in profileBytes)
                                {
                                    for (int attempt = 0; attempt < 2; attempt++)
                                    {
                                        Array.Clear(setBuf, 0, setBuf.Length);
                                        setBuf[0] = rid;
                                        setBuf[1] = 21; // 0x15: Battery Query Opcode
                                        setBuf[4] = 1;
                                        setBuf[7] = prof;

                                        if (HidD_SetFeature(hDev, setBuf, setBuf.Length))
                                        {
                                            System.Threading.Thread.Sleep(120);
                                            byte[] getBuf = new byte[setBuf.Length];
                                            getBuf[0] = rid;
                                            if (HidD_GetFeature(hDev, getBuf, getBuf.Length))
                                            {
                                                int b9 = getBuf[9];
                                                int b10 = getBuf[10];
                                                int b11 = getBuf[11];
                                                int b12 = getBuf[12];

                                                int detectedBat = -1;
                                                bool detectedCharging = false;

                                                // 1. オフセット9が残量の場合 (Sprime公式仕様)
                                                if (b9 > 0 && b9 <= 100)
                                                {
                                                    detectedBat = b11 == 1 ? 100 : b9;
                                                    detectedCharging = b10 == 1;
                                                }
                                                // 2. オフセット10が残量の場合 (別リビジョン等)
                                                else if (b10 > 0 && b10 <= 100)
                                                {
                                                    detectedBat = b10;
                                                    detectedCharging = b11 == 1;
                                                }
                                                // 3. マウス接続中(online=1)だが残量0が返ってきた場合
                                                else if (b12 == 1 && b9 == 0 && b10 == 0)
                                                {
                                                    detectedBat = 0;
                                                    detectedCharging = false;
                                                }

                                                if (detectedBat >= 0)
                                                {
                                                    batteryLevel = detectedBat;
                                                    isCharging = detectedCharging;
                                                    break;
                                                }
                                            }
                                        }

                                        if (batteryLevel >= 0) break;
                                        System.Threading.Thread.Sleep(30);
                                    }

                                    if (batteryLevel >= 0) break;
                                }

                                if (batteryLevel >= 0) break;
                            }
                        }

                        // --- プロトコル B: Compx / Nordic 52840 (ATK, VXE, VGN, Zaopin, Scyrox 等) ---
                        if (batteryLevel < 0 && caps.OutputReportByteLength >= 17 && caps.InputReportByteLength >= 17)
                        {
                            byte[] outBuf = new byte[caps.OutputReportByteLength];
                            outBuf[0] = 8;
                            outBuf[1] = 4;
                            outBuf[16] = 73; // チェックサム 0x49
                            if (HidD_SetOutputReport(hDev, outBuf, outBuf.Length))
                            {
                                System.Threading.Thread.Sleep(80);
                                byte[] inBuf = new byte[caps.InputReportByteLength];
                                inBuf[0] = 8;
                                if (HidD_GetInputReport(hDev, inBuf, inBuf.Length))
                                {
                                    if (inBuf[0] == 8 && inBuf[1] == 4)
                                    {
                                        int bat = inBuf[6];
                                        bool wired = inBuf[7] == 1;
                                        if (bat >= 0 && bat <= 100)
                                        {
                                            batteryLevel = bat;
                                            isCharging = wired && bat < 100;
                                        }
                                    }
                                }
                            }
                        }

                        // --- プロトコル C: Nordic 54L15 (ATK Zero 64バイトレポート) ---
                        if (batteryLevel < 0 && caps.OutputReportByteLength >= 64 && caps.InputReportByteLength >= 64)
                        {
                            byte[] outBuf = new byte[caps.OutputReportByteLength];
                            outBuf[0] = 8;
                            outBuf[1] = 0x7D;
                            outBuf[2] = 0x72;
                            outBuf[3] = 0x02;
                            outBuf[5] = 0x01; // 2.4Gドングルリンク
                            outBuf[6] = 0x07; // バッテリークエリ
                            outBuf[7] = 0x01;
                            if (HidD_SetOutputReport(hDev, outBuf, outBuf.Length))
                            {
                                System.Threading.Thread.Sleep(80);
                                byte[] inBuf = new byte[caps.InputReportByteLength];
                                inBuf[0] = 8;
                                if (HidD_GetInputReport(hDev, inBuf, inBuf.Length))
                                {
                                    if (inBuf[1] == 0x72 && inBuf[5] == 0x07 && inBuf[2] == 0x00)
                                    {
                                        int bat = inBuf[7];
                                        if (bat >= 0 && bat <= 100)
                                        {
                                            batteryLevel = bat;
                                            isCharging = false;
                                        }
                                    }
                                }
                            }
                        }

                        // --- プロトコル D: Lamzu (65バイト Feature Report) ---
                        if (batteryLevel < 0 && caps.FeatureReportByteLength >= 65)
                        {
                            byte[] setBuf = new byte[caps.FeatureReportByteLength];
                            setBuf[0] = 0x00;
                            setBuf[3] = 0x02;
                            setBuf[4] = 0x02;
                            setBuf[6] = 0x83; // バッテリーコマンド
                            if (HidD_SetFeature(hDev, setBuf, setBuf.Length))
                            {
                                System.Threading.Thread.Sleep(80);
                                byte[] getBuf = new byte[caps.FeatureReportByteLength];
                                getBuf[0] = 0x00;
                                if (HidD_GetFeature(hDev, getBuf, getBuf.Length))
                                {
                                    if (getBuf[1] == 0xA1 && getBuf[6] == 0x83)
                                    {
                                        int bat = getBuf[8];
                                        bool chg = getBuf[7] == 1;
                                        if (bat >= 0 && bat <= 100)
                                        {
                                            batteryLevel = bat;
                                            isCharging = chg && bat < 100;
                                        }
                                    }
                                }
                            }
                        }

                        // キャッシュの更新または復元（公式Webドライバの battery > 0 ? cache : old 仕様）
                        if (batteryLevel > 0)
                        {
                            _batteryCache[vidPidKey] = (batteryLevel, isCharging);
                        }
                        else if (batteryLevel <= 0 && _batteryCache.TryGetValue(vidPidKey, out var cached))
                        {
                            batteryLevel = cached.Battery;
                            isCharging = cached.IsCharging;
                        }
                        else if (batteryLevel < 0 && KnownModels.ContainsKey((vid, pid)))
                        {
                            // 既知のモデルであれば、一時的なクエリ無応答でもデバイス登録を維持
                            batteryLevel = 100;
                            isCharging = false;
                        }

                        // バッテリー残量が正常に取得できた場合（または既知モデル）、デバイス情報を構築
                        if (batteryLevel >= 0 && batteryLevel <= 100)
                        {
                            queriedVidPids.Add(vidPidKey);

                            string name = ResolveDeviceName(hDev, vid, pid);
                            var cat = GuessCategory(name);

                            list.Add(new DeviceBatteryInfo
                            {
                                Id = $"WEBHID_{vid:X4}_{pid:X4}",
                                Name = name,
                                Category = cat,
                                BatteryLevel = batteryLevel,
                                IsCharging = isCharging,
                                IsConnected = true,
                                IsLowBattery = batteryLevel <= settings.LowBatteryThreshold && batteryLevel > settings.CriticalBatteryThreshold,
                                IsCriticalBattery = batteryLevel <= settings.CriticalBatteryThreshold
                            });
                        }
                    }
                    finally
                    {
                        CloseHandle(hDev);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailData);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }

        return list;
    }

    private static string ResolveDeviceName(IntPtr hDev, ushort vid, ushort pid)
    {
        // 1. 既知のモデル定義テーブルを参照（ドングル "Sprime 1K Receiver" 等を "Sprime PM1" 等の正式名に統一）
        if (KnownModels.TryGetValue((vid, pid), out var knownName))
        {
            return knownName;
        }

        // 2. USBディスクリプタから製品名文字列を取得
        StringBuilder sbProd = new StringBuilder(256);
        if (HidD_GetProductString(hDev, sbProd, sbProd.Capacity))
        {
            string rawProd = sbProd.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(rawProd))
            {
                // ドングルやレシーバーのサフィックスを除去して本体名に整形
                string cleaned = CleanReceiverSuffix(rawProd);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    return cleaned;
                }
            }
        }

        // 3. 製造元文字列の取得
        StringBuilder sbMfg = new StringBuilder(256);
        if (HidD_GetManufacturerString(hDev, sbMfg, sbMfg.Capacity))
        {
            string rawMfg = sbMfg.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(rawMfg))
            {
                return $"{rawMfg} Wireless Device";
            }
        }

        return "Wireless Gaming Device";
    }

    private static string CleanReceiverSuffix(string name)
    {
        string[] suffixes = new[]
        {
            " 4K Receiver", " 8K Receiver", " 1K Receiver",
            " 4K Dongle", " 8K Dongle", " 1K Dongle",
            " Receiver", " Dongle", " 2.4G", " Wireless Receiver", " Wireless Dongle"
        };

        string cleaned = name;
        foreach (var suf in suffixes)
        {
            int idx = cleaned.LastIndexOf(suf, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = cleaned.Substring(0, idx) + cleaned.Substring(idx + suf.Length);
            }
        }

        cleaned = cleaned.Trim();
        if (cleaned.Equals("Sprime", StringComparison.OrdinalIgnoreCase))
        {
            return "Sprime PM1";
        }

        return cleaned;
    }

    private async Task<List<DeviceBatteryInfo>> QueryPnpDevicesAsync(OmniGlanceSettings settings)
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

        // 左右個別TWSイヤホン判定
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
        if (lower.Contains("keyboard") || lower.Contains("keychron") || lower.Contains("atk75") || lower.Contains("atk68") || lower.Contains("wooting") ||
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

        // デフォルトはマウス（Sprime, Ninjutso, Lamzu, Pulsar, ATK, VXE, SteelSeries, Corsair等のマウス）
        return DeviceCategory.Mouse;
    }
}
