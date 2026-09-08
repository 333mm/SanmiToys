using System.Windows.Media;
using Wpf.Ui.Controls;

namespace SanmiToys.Modules.OmniGlance.Models;

public enum IslandAlertType
{
    LowBattery,
    CriticalBattery,
    CpuTemperature,
    GpuTemperature,
    MemoryUsage
}

public class IslandAlertInfo
{
    public IslandAlertType Type { get; set; } = IslandAlertType.LowBattery;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string LevelText { get; set; } = string.Empty;
    public string BadgeText { get; set; } = "WARN";
    public SymbolRegular Symbol { get; set; } = SymbolRegular.Warning24;
    public bool IsMouse { get; set; }
    public Color AlertColor { get; set; } = Color.FromRgb(0xFF, 0x4D, 0x4F);
}
