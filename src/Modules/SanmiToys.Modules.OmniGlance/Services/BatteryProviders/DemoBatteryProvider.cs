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
                Id = "DEMO_HEADSET",
                Name = "Studio Wireless Pro",
                Category = DeviceCategory.Headset,
                BatteryLevel = 18,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = true,
                IsCriticalBattery = false
            },
            new()
            {
                Id = "DEMO_KEYBOARD",
                Name = "Mechanical Keyboard",
                Category = DeviceCategory.Keyboard,
                BatteryLevel = 92,
                IsCharging = false,
                IsConnected = true,
                IsLowBattery = false,
                IsCriticalBattery = false
            }
        };

        return Task.FromResult(result);
    }
}
