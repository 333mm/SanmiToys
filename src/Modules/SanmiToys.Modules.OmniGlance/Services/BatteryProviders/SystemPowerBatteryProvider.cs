using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class SystemPowerBatteryProvider : IBatteryProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    public string ProviderName => "SystemPower";

    public Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>();
        try
        {
            if (GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryLifePercent != 255)
            {
                int pcLevel = Math.Clamp((int)power.BatteryLifePercent, 0, 100);
                bool isCharging = power.ACLineStatus == 1;

                result.Add(new DeviceBatteryInfo
                {
                    Id = "PC_BATTERY",
                    Name = "Laptop Battery",
                    Category = DeviceCategory.Laptop,
                    BatteryLevel = pcLevel,
                    IsCharging = isCharging,
                    IsConnected = true,
                    IsLowBattery = !isCharging && pcLevel <= settings.LowBatteryThreshold && pcLevel > settings.CriticalBatteryThreshold,
                    IsCriticalBattery = !isCharging && pcLevel <= settings.CriticalBatteryThreshold
                });
            }
        }
        catch { }

        return Task.FromResult(result);
    }
}
