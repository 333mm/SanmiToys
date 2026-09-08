using System.Collections.Generic;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services.BatteryProviders;

public interface IBatteryProvider
{
    string ProviderName { get; }
    Task<List<DeviceBatteryInfo>> GetDevicesAsync(OmniGlanceSettings settings);
}
