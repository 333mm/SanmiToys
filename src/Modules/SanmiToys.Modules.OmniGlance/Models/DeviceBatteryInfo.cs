using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace SanmiToys.Modules.OmniGlance.Models;

public enum DeviceCategory
{
    Mouse,
    Keyboard,
    Headset,
    Headphones,
    Earbuds,
    EarbudLeft,
    EarbudRight,
    Controller,
    Pen,
    Laptop,
    Phone,
    Generic
}

public class DeviceBatteryInfo : INotifyPropertyChanged
{
    private static readonly SolidColorBrush CyanBrush = CreateFrozenBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly SolidColorBrush CyanBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0x22, 0xD3, 0xEE));

    private static readonly SolidColorBrush VioletBrush = CreateFrozenBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
    private static readonly SolidColorBrush VioletBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xA7, 0x8B, 0xFA));

    private static readonly SolidColorBrush YellowBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
    private static readonly SolidColorBrush YellowBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xFF, 0xCC, 0x00));

    private static readonly SolidColorBrush EmeraldBrush = CreateFrozenBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush EmeraldBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0x34, 0xD3, 0x99));

    private static readonly SolidColorBrush SkyBrush = CreateFrozenBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly SolidColorBrush SkyBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0x38, 0xBD, 0xF8));

    private static readonly SolidColorBrush RoseBrush = CreateFrozenBrush(Color.FromRgb(0xFB, 0x71, 0x85));
    private static readonly SolidColorBrush RoseBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xFB, 0x71, 0x85));

    private static readonly SolidColorBrush OrangeBrush = CreateFrozenBrush(Color.FromRgb(0xF9, 0x73, 0x16));
    private static readonly SolidColorBrush OrangeBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xF9, 0x73, 0x16));

    private static readonly SolidColorBrush PinkBrush = CreateFrozenBrush(Color.FromRgb(0xF4, 0x72, 0xB6));
    private static readonly SolidColorBrush PinkBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xF4, 0x72, 0xB6));

    private static readonly SolidColorBrush GenericBrush = CreateFrozenBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private static readonly SolidColorBrush GenericBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0x4C, 0xC2, 0xFF));

    private static readonly SolidColorBrush CriticalBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x4D, 0x4F));
    private static readonly SolidColorBrush CriticalBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xFF, 0x4D, 0x4F));

    private static readonly SolidColorBrush LowBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xA9, 0x40));
    private static readonly SolidColorBrush LowBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xFF, 0xA9, 0x40));

    private static readonly SolidColorBrush ChargingBrush = CreateFrozenBrush(Color.FromRgb(0x52, 0xC4, 0x1A));
    private static readonly SolidColorBrush ChargingBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0x52, 0xC4, 0x1A));

    private static readonly SolidColorBrush MonochromeBrush = CreateFrozenBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly SolidColorBrush MonochromeBgBrush = CreateFrozenBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    public static bool IsMonochromeMode { get; set; } = false;

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private int _batteryLevel;
    private bool _isCharging;
    private bool _isConnected;
    private bool _isLowBattery;
    private bool _isCriticalBattery;
    private DeviceCategory _category = DeviceCategory.Generic;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public DeviceCategory Category
    {
        get => _category;
        set
        {
            if (_category != value)
            {
                _category = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMouse));
                OnPropertyChanged(nameof(IsKeyboard));
                OnPropertyChanged(nameof(IsHeadphones));
                OnPropertyChanged(nameof(IsEarbuds));
                OnPropertyChanged(nameof(IsEarbudLeft));
                OnPropertyChanged(nameof(IsEarbudRight));
                OnPropertyChanged(nameof(IsAnyEarbud));
                OnPropertyChanged(nameof(Symbol));
                OnPropertyChanged(nameof(DisplayBrush));
                OnPropertyChanged(nameof(DisplayBackgroundBrush));
            }
        }
    }

    public bool IsMouse => Category == DeviceCategory.Mouse;
    public bool IsKeyboard => Category == DeviceCategory.Keyboard;
    public bool IsHeadphones => Category is DeviceCategory.Headphones or DeviceCategory.Headset;
    public bool IsEarbuds => Category == DeviceCategory.Earbuds;
    public bool IsEarbudLeft => Category == DeviceCategory.EarbudLeft;
    public bool IsEarbudRight => Category == DeviceCategory.EarbudRight;
    public bool IsAnyEarbud => Category is DeviceCategory.Earbuds or DeviceCategory.EarbudLeft or DeviceCategory.EarbudRight;
    public int LastAlertedLevel { get; set; } = -1;

    private bool _isOptionEnabled = true;
    public bool IsOptionEnabled
    {
        get => _isOptionEnabled;
        set
        {
            if (_isOptionEnabled != value)
            {
                _isOptionEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public int BatteryLevel
    {
        get => _batteryLevel;
        set
        {
            if (_batteryLevel != value)
            {
                _batteryLevel = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BatteryText));
                OnPropertyChanged(nameof(DisplayBrush));
                OnPropertyChanged(nameof(DisplayBackgroundBrush));
            }
        }
    }

    public bool IsCharging
    {
        get => _isCharging;
        set
        {
            if (_isCharging != value)
            {
                _isCharging = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayBrush));
                OnPropertyChanged(nameof(DisplayBackgroundBrush));
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsLowBattery
    {
        get => _isLowBattery;
        set
        {
            if (_isLowBattery != value)
            {
                _isLowBattery = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayBrush));
                OnPropertyChanged(nameof(DisplayBackgroundBrush));
            }
        }
    }

    public bool IsCriticalBattery
    {
        get => _isCriticalBattery;
        set
        {
            if (_isCriticalBattery != value)
            {
                _isCriticalBattery = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayBrush));
                OnPropertyChanged(nameof(DisplayBackgroundBrush));
            }
        }
    }

    public string BatteryText => $"{BatteryLevel}%";

    public SolidColorBrush DisplayBrush
    {
        get
        {
            if (IsCriticalBattery) return CriticalBrush;
            if (IsLowBattery) return LowBrush;
            if (IsCharging) return ChargingBrush;
            if (IsMonochromeMode) return MonochromeBrush;
            return Category switch
            {
                DeviceCategory.Mouse => CyanBrush,
                DeviceCategory.Keyboard => VioletBrush,
                DeviceCategory.Headphones or DeviceCategory.Headset => YellowBrush,
                DeviceCategory.Earbuds => EmeraldBrush,
                DeviceCategory.EarbudLeft => SkyBrush,
                DeviceCategory.EarbudRight => RoseBrush,
                DeviceCategory.Controller => OrangeBrush,
                DeviceCategory.Pen => PinkBrush,
                _ => GenericBrush
            };
        }
    }

    public SolidColorBrush DisplayBackgroundBrush
    {
        get
        {
            if (IsCriticalBattery) return CriticalBgBrush;
            if (IsLowBattery) return LowBgBrush;
            if (IsCharging) return ChargingBgBrush;
            if (IsMonochromeMode) return MonochromeBgBrush;
            return Category switch
            {
                DeviceCategory.Mouse => CyanBgBrush,
                DeviceCategory.Keyboard => VioletBgBrush,
                DeviceCategory.Headphones or DeviceCategory.Headset => YellowBgBrush,
                DeviceCategory.Earbuds => EmeraldBgBrush,
                DeviceCategory.EarbudLeft => SkyBgBrush,
                DeviceCategory.EarbudRight => RoseBgBrush,
                DeviceCategory.Controller => OrangeBgBrush,
                DeviceCategory.Pen => PinkBgBrush,
                _ => GenericBgBrush
            };
        }
    }

    public void RefreshDisplayBrushes()
    {
        OnPropertyChanged(nameof(DisplayBrush));
        OnPropertyChanged(nameof(DisplayBackgroundBrush));
    }

    public SymbolRegular Symbol => Category switch
    {
        DeviceCategory.Mouse => SymbolRegular.CursorHover24,
        DeviceCategory.Keyboard => SymbolRegular.Keyboard24,
        DeviceCategory.Headset or DeviceCategory.Headphones => SymbolRegular.Headphones24,
        DeviceCategory.Earbuds or DeviceCategory.EarbudLeft or DeviceCategory.EarbudRight => SymbolRegular.HeadphonesSoundWave24,
        DeviceCategory.Controller => SymbolRegular.Games24,
        DeviceCategory.Pen => SymbolRegular.Pen24,
        DeviceCategory.Laptop => SymbolRegular.Laptop24,
        DeviceCategory.Phone => SymbolRegular.Phone24,
        _ => SymbolRegular.BatteryCharge24
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
