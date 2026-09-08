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
                        double cpu = (1.0 - ((double)idleDelta / totalSys)) * 100.0;
                        PerformanceInfo.CpuUsage = Math.Clamp(cpu, 0.0, 100.0);
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
        }
        catch
        {
            // パフォーマンス取得例外は静かに無視
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
                    IntPtr pBuffer = Marshal.AllocHGlobal((int)bufferSize);
                    try
                    {
                        if (PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, pBuffer) == 0)
                        {
                            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                            double sum = 0.0;
                            for (int i = 0; i < itemCount; i++)
                            {
                                IntPtr itemPtr = IntPtr.Add(pBuffer, i * itemSize);
                                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(itemPtr);
                                if (item.FmtValue.CStatus == 0 && item.FmtValue.DoubleValue > 0)
                                {
                                    sum += item.FmtValue.DoubleValue;
                                }
                            }
                            PerformanceInfo.GpuUsage = Math.Clamp(sum, 0.0, 100.0);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pBuffer);
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
    }
}
