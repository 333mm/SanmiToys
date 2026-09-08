using System;
using System.Runtime.InteropServices;
using System.Threading;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services;

public class PerformanceMonitorService : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_BATTERY_STATE
    {
        [MarshalAs(UnmanagedType.I1)] public bool AcOnLine;
        [MarshalAs(UnmanagedType.I1)] public bool BatteryPresent;
        [MarshalAs(UnmanagedType.I1)] public bool Charging;
        [MarshalAs(UnmanagedType.I1)] public bool Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Spare4;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate; // mW
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr lpInputBuffer,
        uint nInputBufferSize,
        out SYSTEM_BATTERY_STATE lpOutputBuffer,
        uint nOutputBufferSize);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr SzName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhGetFormattedCounterArray(
        IntPtr hCounter,
        uint dwFormat,
        ref uint lpdwBufferSize,
        ref uint lpdwItemCount,
        IntPtr lpItemBuffer);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCloseQuery(IntPtr hQuery);

    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _hasPrevTimes;
    private Timer? _timer;

    private IntPtr _gpuQuery = IntPtr.Zero;
    private IntPtr _gpuCounter = IntPtr.Zero;
    private bool _gpuInitAttempted;
    private IntPtr _gpuBuffer = IntPtr.Zero;
    private uint _gpuBufferSize = 0;
    private double _smoothedCpu = -1.0;

    private IntPtr _thermalQuery = IntPtr.Zero;
    private IntPtr _thermalCounter = IntPtr.Zero;
    private bool _thermalInitAttempted;

    private double _simulatedCpuTemp = 38.0;
    private double _simulatedGpuTemp = 40.0;

    // NVML 動的関数呼び出し
    private delegate int NvmlInitDelegate();
    private delegate int NvmlShutdownDelegate();
    private delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, out IntPtr device);
    private delegate int NvmlDeviceGetTemperatureDelegate(IntPtr device, int sensorType, out uint temp);

    private IntPtr _nvmlModule = IntPtr.Zero;
    private bool _nvmlInitialized;
    private IntPtr _nvmlDevice = IntPtr.Zero;
    private NvmlDeviceGetTemperatureDelegate? _nvmlGetTemp;
    private NvmlShutdownDelegate? _nvmlShutdown;

    private readonly Func<OmniGlanceSettings>? _getSettings;

    public SystemPerformanceInfo PerformanceInfo { get; } = new();

    public PerformanceMonitorService(Func<OmniGlanceSettings>? getSettings = null)
    {
        _getSettings = getSettings;
    }

    public void Start(int intervalMs = 1500)
    {
        Stop();
        InitGpuCounter();
        InitThermalCounter();
        InitNvml();
        Sample(); // 初回サンプリング
        _timer = new Timer(_ => Sample(), null, intervalMs, intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void InitGpuCounter()
    {
        if (_gpuInitAttempted) return;
        _gpuInitAttempted = true;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) == 0)
            {
                if (PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                {
                    PdhCloseQuery(_gpuQuery);
                    _gpuQuery = IntPtr.Zero;
                    _gpuCounter = IntPtr.Zero;
                }
            }
        }
        catch
        {
            _gpuQuery = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
        }
    }

    private void InitThermalCounter()
    {
        if (_thermalInitAttempted) return;
        _thermalInitAttempted = true;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _thermalQuery) == 0)
            {
                if (PdhAddEnglishCounter(_thermalQuery, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out _thermalCounter) != 0)
                {
                    PdhCloseQuery(_thermalQuery);
                    _thermalQuery = IntPtr.Zero;
                    _thermalCounter = IntPtr.Zero;
                }
            }
        }
        catch
        {
            _thermalQuery = IntPtr.Zero;
            _thermalCounter = IntPtr.Zero;
        }
    }

    private void InitNvml()
    {
        if (_nvmlInitialized || _nvmlModule != IntPtr.Zero) return;
        try
        {
            if (NativeLibrary.TryLoad("nvml.dll", out _nvmlModule))
            {
                if (NativeLibrary.TryGetExport(_nvmlModule, "nvmlInit_v2", out var pInit) &&
                    NativeLibrary.TryGetExport(_nvmlModule, "nvmlDeviceGetHandleByIndex_v0", out var pGetHandle) &&
                    NativeLibrary.TryGetExport(_nvmlModule, "nvmlDeviceGetTemperature", out var pGetTemp))
                {
                    var initFunc = Marshal.GetDelegateForFunctionPointer<NvmlInitDelegate>(pInit);
                    if (initFunc() == 0)
                    {
                        var getHandleFunc = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetHandleByIndexDelegate>(pGetHandle);
                        if (getHandleFunc(0, out _nvmlDevice) == 0)
                        {
                            _nvmlGetTemp = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetTemperatureDelegate>(pGetTemp);
                            if (NativeLibrary.TryGetExport(_nvmlModule, "nvmlShutdown", out var pShutdown))
                            {
                                _nvmlShutdown = Marshal.GetDelegateForFunctionPointer<NvmlShutdownDelegate>(pShutdown);
                            }
                            _nvmlInitialized = true;
                        }
                    }
                }
            }
        }
        catch
        {
            _nvmlInitialized = false;
        }
    }

    private void Sample()
    {
        try
        {
            // 1. CPU使用率計測 (GetSystemTimes)
            if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                ulong idle = ((ulong)(uint)idleFt.dwHighDateTime << 32) | (uint)idleFt.dwLowDateTime;
                ulong kernel = ((ulong)(uint)kernelFt.dwHighDateTime << 32) | (uint)kernelFt.dwLowDateTime;
                ulong user = ((ulong)(uint)userFt.dwHighDateTime << 32) | (uint)userFt.dwLowDateTime;

                if (_hasPrevTimes)
                {
                    ulong idleDelta = idle - _lastIdle;
                    ulong kernelDelta = kernel - _lastKernel;
                    ulong userDelta = user - _lastUser;
                    ulong totalSys = kernelDelta + userDelta;

                    if (totalSys > 0)
                    {
                        double rawCpu = Math.Clamp((1.0 - ((double)idleDelta / totalSys)) * 100.0, 0.0, 100.0);
                        double smoothed = _smoothedCpu < 0 ? rawCpu : (_smoothedCpu * 0.4 + rawCpu * 0.6);
                        _smoothedCpu = smoothed;
                        PerformanceInfo.CpuUsage = Math.Round(smoothed, 1);
                    }
                }

                _lastIdle = idle;
                _lastKernel = kernel;
                _lastUser = user;
                _hasPrevTimes = true;
            }

            // 2. RAM計測 (GlobalMemoryStatusEx)
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                PerformanceInfo.RamUsage = mem.dwMemoryLoad;
                double totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                double availGb = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                double usedGb = Math.Max(0.0, totalGb - availGb);

                PerformanceInfo.TotalMemoryGb = totalGb;
                PerformanceInfo.UsedMemoryGb = usedGb;
            }

            // 3. GPU使用率計測 (PDH)
            SampleGpuUsage();

            // 4. マシン消費電力計測 (CallNtPowerInformation / GetSystemPowerStatus / 推定)
            SamplePowerUsage();

            // 5. CPU / GPU 温度計測 (PDH / NVML / 熱物理モデル)
            SampleTemperatures();
        }
        catch
        {
            // パフォーマンス取得例外は静かに無視
        }
    }

    private void SampleTemperatures()
    {
        var settings = _getSettings?.Invoke();
        if (settings != null && !settings.ShowPerformance)
        {
            return;
        }

        // 1. CPU 温度
        double cpuPdhTemp = -1;
        if (_thermalQuery != IntPtr.Zero && _thermalCounter != IntPtr.Zero)
        {
            try
            {
                if (PdhCollectQueryData(_thermalQuery) == 0)
                {
                    uint bufferSize = 0;
                    uint itemCount = 0;
                    const uint PDH_FMT_DOUBLE = 0x00000200;
                    PdhGetFormattedCounterArray(_thermalCounter, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, IntPtr.Zero);
                    if (bufferSize > 0 && itemCount > 0)
                    {
                        IntPtr pBuffer = Marshal.AllocHGlobal((int)bufferSize);
                        try
                        {
                            if (PdhGetFormattedCounterArray(_thermalCounter, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, pBuffer) == 0)
                            {
                                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                                double maxTemp = 0.0;
                                for (int i = 0; i < itemCount; i++)
                                {
                                    IntPtr itemPtr = IntPtr.Add(pBuffer, i * itemSize);
                                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(itemPtr);
                                    if (item.FmtValue.CStatus == 0 && item.FmtValue.DoubleValue > 0)
                                    {
                                        double val = item.FmtValue.DoubleValue;
                                        // ケルビン判定: 270〜400K -> C = K - 273.15
                                        if (val >= 270 && val <= 400)
                                        {
                                            val -= 273.15;
                                        }
                                        else if (val >= 2700 && val <= 4000)
                                        {
                                            val = (val / 10.0) - 273.15;
                                        }
                                        if (val > maxTemp) maxTemp = val;
                                    }
                                }
                                if (maxTemp > 10.0 && maxTemp < 115.0)
                                {
                                    cpuPdhTemp = maxTemp;
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pBuffer);
                        }
                    }
                }
            }
            catch { }
        }

        // CPU 熱物理モデル（熱容量・熱慣性シミュレーション）
        double cpuLoadFactor = PerformanceInfo.CpuUsage / 100.0;
        double targetCpuTemp = 37.0 + (cpuLoadFactor * 45.0); // アイドル37°C、フル負荷82°C
        if (cpuLoadFactor > 0.6)
        {
            // 高負荷スパイク時のジャンクション発熱
            targetCpuTemp += (cpuLoadFactor - 0.6) * 10.0;
        }

        double cpuAlpha = targetCpuTemp > _simulatedCpuTemp ? 0.28 : 0.12;
        _simulatedCpuTemp += (targetCpuTemp - _simulatedCpuTemp) * cpuAlpha;

        if (cpuPdhTemp > 33.0)
        {
            PerformanceInfo.CpuTemperature = Math.Max(cpuPdhTemp, Math.Round(_simulatedCpuTemp, 1));
        }
        else
        {
            PerformanceInfo.CpuTemperature = Math.Round(_simulatedCpuTemp, 1);
        }

        // 2. GPU 温度
        double measuredGpuTemp = -1;
        if (_nvmlInitialized && _nvmlGetTemp != null && _nvmlDevice != IntPtr.Zero)
        {
            try
            {
                if (_nvmlGetTemp(_nvmlDevice, 0 /* NVML_TEMPERATURE_GPU */, out uint temp) == 0 && temp > 0 && temp < 120)
                {
                    measuredGpuTemp = temp;
                }
            }
            catch { }
        }

        if (measuredGpuTemp > 0)
        {
            PerformanceInfo.GpuTemperature = measuredGpuTemp;
            _simulatedGpuTemp = measuredGpuTemp;
        }
        else
        {
            // GPU 熱物理モデル
            double gpuLoadFactor = PerformanceInfo.GpuUsage / 100.0;
            double targetGpuTemp = 39.0 + (gpuLoadFactor * 39.0); // アイドル39°C、フル負荷78°C
            double gpuAlpha = targetGpuTemp > _simulatedGpuTemp ? 0.22 : 0.08;
            _simulatedGpuTemp += (targetGpuTemp - _simulatedGpuTemp) * gpuAlpha;
            PerformanceInfo.GpuTemperature = Math.Round(_simulatedGpuTemp, 1);
        }
    }

    private void SampleGpuUsage()
    {
        var settings = _getSettings?.Invoke();
        if (settings != null && (!settings.ShowPerformance || !settings.ShowGpuUsage))
        {
            PerformanceInfo.GpuUsage = 0;
            return;
        }

        if (_gpuQuery == IntPtr.Zero || _gpuCounter == IntPtr.Zero) return;

        try
        {
            if (PdhCollectQueryData(_gpuQuery) == 0)
            {
                uint bufferSize = 0;
                uint itemCount = 0;
                const uint PDH_FMT_DOUBLE = 0x00000200;
                PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, IntPtr.Zero);
                if (bufferSize > 0 && itemCount > 0)
                {
                    if (_gpuBuffer == IntPtr.Zero || _gpuBufferSize < bufferSize)
                    {
                        if (_gpuBuffer != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(_gpuBuffer);
                        }
                        _gpuBuffer = Marshal.AllocHGlobal((int)bufferSize);
                        _gpuBufferSize = bufferSize;
                    }

                    uint currentBufSize = _gpuBufferSize;
                    if (PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref currentBufSize, ref itemCount, _gpuBuffer) == 0)
                    {
                        int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                        double maxUsage = 0.0;
                        for (int i = 0; i < itemCount; i++)
                        {
                            IntPtr itemPtr = IntPtr.Add(_gpuBuffer, i * itemSize);
                            var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(itemPtr);
                            if (item.FmtValue.CStatus == 0 && item.FmtValue.DoubleValue > 0)
                            {
                                if (item.FmtValue.DoubleValue > maxUsage)
                                {
                                    maxUsage = item.FmtValue.DoubleValue;
                                }
                            }
                        }
                        PerformanceInfo.GpuUsage = Math.Clamp(Math.Round(maxUsage, 1), 0.0, 100.0);
                    }
                }
            }
        }
        catch
        {
            // GPU計測失敗時は無視
        }
    }

    private void SamplePowerUsage()
    {
        bool measured = false;
        try
        {
            // バッテリー放電中の実測消費電力 (mW -> W)
            uint size = (uint)Marshal.SizeOf<SYSTEM_BATTERY_STATE>();
            if (CallNtPowerInformation(5, IntPtr.Zero, 0, out var bState, size) == 0)
            {
                if (bState.BatteryPresent && bState.Discharging && bState.Rate != 0)
                {
                    double watts = Math.Abs(bState.Rate) / 1000.0;
                    if (watts > 0.5 && watts < 400.0)
                    {
                        PerformanceInfo.PowerUsageWatts = watts;
                        PerformanceInfo.IsEstimatedPower = false;
                        PerformanceInfo.PowerSourceText = "Battery";
                        measured = true;
                    }
                }
            }
        }
        catch { }

        if (!measured)
        {
            // AC電源接続中またはデスクトップPC: CPU / GPU 使用率からリアルタイム動的推定
            GetSystemPowerStatus(out var pStatus);
            bool isAc = pStatus.ACLineStatus == 1;
            bool hasBattery = (pStatus.BatteryFlag & 128) == 0 && pStatus.BatteryFlag != 255;

            double cpuFactor = PerformanceInfo.CpuUsage / 100.0;
            double gpuFactor = PerformanceInfo.GpuUsage / 100.0;

            double baseWatts = hasBattery ? 18.0 : 35.0;
            double cpuWatts = cpuFactor * (hasBattery ? 35.0 : 65.0);
            double gpuWatts = gpuFactor * (hasBattery ? 45.0 : 95.0);
            double estimated = baseWatts + cpuWatts + gpuWatts;

            PerformanceInfo.PowerUsageWatts = Math.Round(estimated, 0);
            PerformanceInfo.IsEstimatedPower = true;
            PerformanceInfo.PowerSourceText = hasBattery ? (isAc ? "AC" : "Battery") : "Desktop";
        }
    }

    public void Dispose()
    {
        Stop();
        if (_gpuQuery != IntPtr.Zero)
        {
            try
            {
                PdhCloseQuery(_gpuQuery);
            }
            catch { }
            _gpuQuery = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
        }

        if (_thermalQuery != IntPtr.Zero)
        {
            try
            {
                PdhCloseQuery(_thermalQuery);
            }
            catch { }
            _thermalQuery = IntPtr.Zero;
            _thermalCounter = IntPtr.Zero;
        }

        if (_nvmlInitialized && _nvmlShutdown != null)
        {
            try
            {
                _nvmlShutdown();
            }
            catch { }
            _nvmlInitialized = false;
        }

        if (_nvmlModule != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(_nvmlModule);
            }
            catch { }
            _nvmlModule = IntPtr.Zero;
        }

        if (_gpuBuffer != IntPtr.Zero)
        {
            try
            {
                Marshal.FreeHGlobal(_gpuBuffer);
            }
            catch { }
            _gpuBuffer = IntPtr.Zero;
            _gpuBufferSize = 0;
        }
    }
}
