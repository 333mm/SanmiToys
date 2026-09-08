using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SanmiToys.Modules.OmniGlance.Models;

public class CalendarDayCell : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _hasEvents;

    public DateTime Date { get; set; }
    public int DayNumber => Date.Day;
    public bool IsCurrentMonth { get; set; }
    public bool IsToday { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BackgroundBrush));
                OnPropertyChanged(nameof(BorderBrush));
            }
        }
    }

    public bool HasEvents
    {
        get => _hasEvents;
        set
        {
            if (_hasEvents != value)
            {
                _hasEvents = value;
                OnPropertyChanged();
            }
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<Brush> EventDotBrushes { get; } = new();

    public Brush ForegroundBrush
    {
        get
        {
            if (IsToday)
            {
                return Brushes.White;
            }

            if (!IsCurrentMonth)
            {
                return new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            }

            if (Date.DayOfWeek == DayOfWeek.Sunday)
            {
                return new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            }

            if (Date.DayOfWeek == DayOfWeek.Saturday)
            {
                return new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
            }

            return new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        }
    }

    public Brush BackgroundBrush
    {
        get
        {
            if (IsToday)
            {
                return new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x78, 0xD4));
            }
            if (IsSelected)
            {
                return new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            }
            return Brushes.Transparent;
        }
    }

    public Brush BorderBrush
    {
        get
        {
            if (IsToday)
            {
                return new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
            }
            if (IsSelected)
            {
                return new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            }
            return Brushes.Transparent;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
