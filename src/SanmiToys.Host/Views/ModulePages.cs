using System.Windows.Controls;
using SanmiToys.Core.Interfaces;

namespace SanmiToys.Host.Views;

public class FluidDragPage : Page
{
    public FluidDragPage(IToyModule module)
    {
        Title = "FluidDrag";
        Content = module.CreateSettingsView();
    }
}

public class FocusDimmerPage : Page
{
    public FocusDimmerPage(IToyModule module)
    {
        Title = "FocusDimmer";
        Content = module.CreateSettingsView();
    }
}

public class SnapTransPage : Page
{
    public SnapTransPage(IToyModule module)
    {
        Title = "SnapTrans";
        Content = module.CreateSettingsView();
    }
}

public class SwiftVolumePage : Page
{
    public SwiftVolumePage(IToyModule module)
    {
        Title = "SwiftVolume";
        Content = module.CreateSettingsView();
    }
}
