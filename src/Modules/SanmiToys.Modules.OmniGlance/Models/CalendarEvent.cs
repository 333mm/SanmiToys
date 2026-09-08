using System;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace SanmiToys.Modules.OmniGlance.Models;

public class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string CalendarId { get; set; } = string.Empty;
    public string CalendarName { get; set; } = string.Empty;
    public CalendarProviderType ProviderType { get; set; } = CalendarProviderType.Local;
    public string ExternalUid { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool IsAllDay { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#4CC2FF";

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

    [JsonIgnore]
    public bool CanEdit => CalendarId == "local" || ProviderType == CalendarProviderType.Local || ProviderType == CalendarProviderType.AppleICloud || ProviderType == CalendarProviderType.GoogleCalendar;

    [JsonIgnore]
    public bool CanDelete => CalendarId == "local" || ProviderType == CalendarProviderType.Local || ProviderType == CalendarProviderType.AppleICloud || ProviderType == CalendarProviderType.GoogleCalendar;

    [JsonIgnore]
    public bool CanOpenWeb => ProviderType == CalendarProviderType.GoogleCalendar || ProviderType == CalendarProviderType.AppleICloud || !string.IsNullOrWhiteSpace(Url);

    [JsonIgnore]
    public string TimeText
    {
        get
        {
            if (IsAllDay) return "終日";
            return $"{StartTime:HH:mm} - {EndTime:HH:mm}";
        }
    }

    [JsonIgnore]
    public bool HasLocation => !string.IsNullOrWhiteSpace(Location);
}
