using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace SanmiToys.Modules.OmniGlance.Models;

public class CalendarSubscription : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString();
    private string _name = "マイカレンダー";
    private string _url = string.Empty;
    private string _colorHex = "#4CC2FF";
    private bool _isEnabled = true;
    private CalendarProviderType _providerType = CalendarProviderType.GenericIcal;
    private string _appleId = string.Empty;
    private string _appSpecificPassword = string.Empty;
    private string _calDavUrl = string.Empty;
    private string _googleClientId = string.Empty;
    private string _googleClientSecret = string.Empty;
    private string _refreshToken = string.Empty;

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Url
    {
        get => _url;
        set { _url = value; OnPropertyChanged(); }
    }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            if (_colorHex != value)
            {
                _colorHex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ColorBrush));
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    public CalendarProviderType ProviderType
    {
        get => _providerType;
        set
        {
            if (_providerType != value)
            {
                _providerType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsICloud));
                OnPropertyChanged(nameof(IsGoogle));
            }
        }
    }

    [JsonIgnore]
    public bool IsICloud => ProviderType == CalendarProviderType.AppleICloud;

    [JsonIgnore]
    public bool IsGoogle => ProviderType == CalendarProviderType.GoogleCalendar;

    public string AppleId
    {
        get => _appleId;
        set { _appleId = value; OnPropertyChanged(); }
    }

    public string AppSpecificPassword
    {
        get => _appSpecificPassword;
        set { _appSpecificPassword = value; OnPropertyChanged(); }
    }

    public string CalDavUrl
    {
        get => _calDavUrl;
        set { _calDavUrl = value; OnPropertyChanged(); }
    }

    public string GoogleClientId
    {
        get => _googleClientId;
        set { _googleClientId = value; OnPropertyChanged(); }
    }

    public string GoogleClientSecret
    {
        get => _googleClientSecret;
        set { _googleClientSecret = value; OnPropertyChanged(); }
    }

    public string RefreshToken
    {
        get => _refreshToken;
        set { _refreshToken = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public Brush ColorBrush
    {
        get
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(ColorHex ?? "#4CC2FF");
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                var brush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
                brush.Freeze();
                return brush;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
