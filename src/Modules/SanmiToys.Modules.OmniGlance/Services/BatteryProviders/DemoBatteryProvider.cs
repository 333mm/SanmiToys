using System.Collections.Generic;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public class DemoBatteryProvider : IBatteryProvider
{
    public string ProviderName => "Demo";

    public Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings)
    {
        var result = new List<DeviceBatteryInfo>
        {
            new()
            {
                Id = "DEMO_MOUSE",
                Name = "Precision Master Mouse",
                Category = DeviceCategory.Mouse,
                BatteryLevel = 78,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_KEYBOARD",
                Name = "Mechanical Wireless Keyboard",
                Category = DeviceCategory.Keyboard,
                BatteryLevel = 92,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_HEADPHONES",
                Name = "Studio Wireless Headphones",
                Category = DeviceCategory.Headphones,
                BatteryLevel = 55,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_EARBUD_L",
                Name = "WF-1000XM5 (L)",
                Category = DeviceCategory.EarbudLeft,
                BatteryLevel = 85,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_EARBUD_R",
                Name = "WF-1000XM5 (R)",
                Category = DeviceCategory.EarbudRight,
                BatteryLevel = 80,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_EARBUDS_PAIR",
                Name = "AirPods Pro (Pair)",
                Category = DeviceCategory.Earbuds,
                BatteryLevel = 100,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_CONTROLLER",
                Name = "Wireless Game Controller",
                Category = DeviceCategory.Controller,
                BatteryLevel = 65,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_PEN",
                Name = "Precision Stylus Pen",
                Category = DeviceCategory.Pen,
                BatteryLevel = 45,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            }
        };

        return Task.FromResult(result);
    }
}
