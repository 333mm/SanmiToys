using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wpf.Ui.Controls;

namespace SanmiToys.Modules.OmniGlance.Models;

public enum DeviceCategory
{
    Mouse,
    Keyboard,
    Headset,
    Controller,
    Pen,
    Laptop,
    Phone,
    Generic
}

public class DeviceBatteryInfo : INotifyPropertyChanged
{
    private int _batteryLevel;
    private bool _isCharging;
    private bool _isConnected;
    private bool _isLowBattery;
    private bool _isCriticalBattery;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DeviceCategory Category { get; set; } = DeviceCategory.Generic;
    public bool IsMouse => Category == DeviceCategory.Mouse;
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
            }
        }
    }

    public string BatteryText => $"{BatteryLevel}%";

    public SymbolRegular Symbol => Category switch
    {
        DeviceCategory.Mouse => SymbolRegular.CursorHover24,
        DeviceCategory.Keyboard => SymbolRegular.Keyboard24,
        DeviceCategory.Headset => SymbolRegular.Headphones24,
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
