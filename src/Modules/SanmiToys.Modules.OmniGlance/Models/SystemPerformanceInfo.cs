using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SanmiToys.Modules.OmniGlance.Models;

public class SystemPerformanceInfo : INotifyPropertyChanged
{
    private double _cpuUsage;
    private double _ramUsage;
    private double _usedMemoryGb;
    private double _totalMemoryGb;

    public double CpuUsage
    {
        get => _cpuUsage;
        set
        {
            if (Math.Abs(_cpuUsage - value) > 0.01)
            {
                _cpuUsage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CpuText));
            }
        }
    }

    public double RamUsage
    {
        get => _ramUsage;
        set
        {
            if (Math.Abs(_ramUsage - value) > 0.01)
            {
                _ramUsage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RamText));
                OnPropertyChanged(nameof(MemoryDetailText));
            }
        }
    }

    public double UsedMemoryGb
    {
        get => _usedMemoryGb;
        set
        {
            if (Math.Abs(_usedMemoryGb - value) > 0.01)
            {
                _usedMemoryGb = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MemoryDetailText));
            }
        }
    }

    public double TotalMemoryGb
    {
        get => _totalMemoryGb;
        set
        {
            if (Math.Abs(_totalMemoryGb - value) > 0.01)
            {
                _totalMemoryGb = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MemoryDetailText));
            }
        }
    }

    private double _gpuUsage;
    private double _powerUsageWatts;
    private bool _isEstimatedPower = true;
    private string _powerSourceText = "AC";

    public double GpuUsage
    {
        get => _gpuUsage;
        set
        {
            if (Math.Abs(_gpuUsage - value) > 0.01)
            {
                _gpuUsage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GpuText));
            }
        }
    }

    public double PowerUsageWatts
    {
        get => _powerUsageWatts;
        set
        {
            if (Math.Abs(_powerUsageWatts - value) > 0.05)
            {
                _powerUsageWatts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PowerText));
            }
        }
    }

    public bool IsEstimatedPower
    {
        get => _isEstimatedPower;
        set
        {
            if (_isEstimatedPower != value)
            {
                _isEstimatedPower = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PowerText));
            }
        }
    }

    public string PowerSourceText
    {
        get => _powerSourceText;
        set
        {
            if (_powerSourceText != value)
            {
                _powerSourceText = value;
                OnPropertyChanged();
            }
        }
    }

    public string CpuText => $"{Math.Round(CpuUsage)}%";
    public string GpuText => $"{Math.Round(GpuUsage)}%";
    public string RamText => $"{Math.Round(RamUsage)}%";
    public string PowerText => $"{Math.Round(PowerUsageWatts)}W";
    public string MemoryDetailText => $"{UsedMemoryGb:F1} / {TotalMemoryGb:F1} GB";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
