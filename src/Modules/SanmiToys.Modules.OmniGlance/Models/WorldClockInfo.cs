using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SanmiToys.Modules.OmniGlance.Models;

public class WorldClockInfo : INotifyPropertyChanged
{
    private string _timeText = string.Empty;
    private string _dayDiffText = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public TimeZoneInfo? TimeZone { get; set; }

    public string TimeText
    {
        get => _timeText;
        set
        {
            if (_timeText != value)
            {
                _timeText = value;
                OnPropertyChanged();
            }
        }
    }

    public string DayDiffText
    {
        get => _dayDiffText;
        set
        {
            if (_dayDiffText != value)
            {
                _dayDiffText = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
