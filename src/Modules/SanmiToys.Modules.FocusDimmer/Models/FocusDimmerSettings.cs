using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace SanmiToys.Modules.FocusDimmer.Models;

public class MonitorProfile : INotifyPropertyChanged
{
    public string DeviceName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    [JsonIgnore] public Screen? ScreenRef { get; set; }

    private double _opacity = 65;
    public double Opacity { get => _opacity; set { _opacity = value; NotifyPropertyChanged(); } }

    private double _margin = 0;
    public double Margin { get => _margin; set { _margin = value; NotifyPropertyChanged(); } }

    private double _delayDarken = 0;
    public double DelayDarken { get => _delayDarken; set { _delayDarken = value; NotifyPropertyChanged(); } }

    private double _durationDarken = 150;
    public double DurationDarken { get => _durationDarken; set { _durationDarken = value; NotifyPropertyChanged(); } }

    private double _durationBrighten = 100;
    public double DurationBrighten { get => _durationBrighten; set { _durationBrighten = value; NotifyPropertyChanged(); } }

    private bool _excludeTaskbar = true;
    public bool ExcludeTaskbar { get => _excludeTaskbar; set { _excludeTaskbar = value; NotifyPropertyChanged(); } }

    private bool _excludeTopmost = false;
    public bool ExcludeTopmost { get => _excludeTopmost; set { _excludeTopmost = value; NotifyPropertyChanged(); } }

    private bool _excludeMaximized = true;
    public bool ExcludeMaximized { get => _excludeMaximized; set { _excludeMaximized = value; NotifyPropertyChanged(); } }

    private bool _useTightFrame = true;
    public bool UseTightFrame { get => _useTightFrame; set { _useTightFrame = value; NotifyPropertyChanged(); } }

    private bool _dimEntirelyWhenInactive = false;
    public bool DimEntirelyWhenInactive { get => _dimEntirelyWhenInactive; set { _dimEntirelyWhenInactive = value; NotifyPropertyChanged(); } }

    private bool _dimDesktopOnly = false;
    public bool DimDesktopOnly { get => _dimDesktopOnly; set { _dimDesktopOnly = value; NotifyPropertyChanged(); } }

    private bool _dimWhenIdle = false;
    public bool DimWhenIdle { get => _dimWhenIdle; set { _dimWhenIdle = value; NotifyPropertyChanged(); } }

    private int _idleTimeout = 30;
    public int IdleTimeout { get => _idleTimeout; set { _idleTimeout = value; NotifyPropertyChanged(); } }

    private double _idleDimOpacity = 80;
    public double IdleDimOpacity { get => _idleDimOpacity; set { _idleDimOpacity = value; NotifyPropertyChanged(); } }

    private string _overlayColorHex = "#000000";
    public string OverlayColorHex { get => _overlayColorHex; set { _overlayColorHex = value; NotifyPropertyChanged(); } }

    private string _ignoreList = "";
    public string IgnoreList { get => _ignoreList; set { _ignoreList = value; NotifyPropertyChanged(); } }

    public void CopyFrom(MonitorProfile other)
    {
        this.Opacity = other.Opacity;
        this.Margin = other.Margin;
        this.DelayDarken = other.DelayDarken;
        this.DurationDarken = other.DurationDarken;
        this.DurationBrighten = other.DurationBrighten;
        this.ExcludeTaskbar = other.ExcludeTaskbar;
        this.ExcludeTopmost = other.ExcludeTopmost;
        this.ExcludeMaximized = other.ExcludeMaximized;
        this.UseTightFrame = other.UseTightFrame;
        this.DimEntirelyWhenInactive = other.DimEntirelyWhenInactive;
        this.DimDesktopOnly = other.DimDesktopOnly;
        this.DimWhenIdle = other.DimWhenIdle;
        this.IdleTimeout = other.IdleTimeout;
        this.IdleDimOpacity = other.IdleDimOpacity;
        this.OverlayColorHex = other.OverlayColorHex;
        this.IgnoreList = other.IgnoreList;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class FocusDimmerSettings
{
    public bool IsEnabled { get; set; } = false;
    public bool AreMonitorsLinked { get; set; } = true;
    public string SelectedMonitorDevice { get; set; } = "";
    public MonitorProfile DefaultProfile { get; set; } = new();
    public List<MonitorProfile> Profiles { get; set; } = new();
    public string AlwaysBrightList { get; set; } = "";
    public string AlwaysDarkList { get; set; } = "amdow, NVIDIA Overlay";
    public string IgnoreList { get; set; } = "";
}
