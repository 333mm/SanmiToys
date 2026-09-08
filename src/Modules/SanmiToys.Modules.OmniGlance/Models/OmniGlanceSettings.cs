using System;
using System.Collections.Generic;

namespace SanmiToys.Modules.OmniGlance.Models;

public enum IslandPositionMode
{
    TopCenter,
    TopRight,
    TopLeft,
    BottomCenter,
    BottomRight,
    BottomLeft,
    LeftCenter,
    RightCenter,
    Custom
}

public class OmniGlanceSettings
{
    public bool IsEnabled { get; set; } = true;
    public IslandPositionMode PositionMode { get; set; } = IslandPositionMode.TopCenter;
    public bool IsPositionLocked { get; set; } = false;
    public double CustomLeft { get; set; } = -1;
    public double CustomTop { get; set; } = -1;
    public bool IsClickThrough { get; set; } = false;
    public bool AutoExpandOnHover { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowPerformance { get; set; } = true;
    public bool ShowCpuUsage { get; set; } = true;
    public bool ShowGpuUsage { get; set; } = true;
    public bool ShowRamUsage { get; set; } = true;
    public bool ShowPowerUsage { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public List<string> DisabledDeviceIds { get; set; } = new();
    public int LowBatteryThreshold { get; set; } = 20;
    public int CriticalBatteryThreshold { get; set; } = 10;
    public bool EnablePulseAnimation { get; set; } = true;
    public bool EnableDemoDevices { get; set; } = true;
    public double IslandScale { get; set; } = 1.0;
    public double Opacity { get; set; } = 0.95;

    // レガシー iCal URL
    public string GoogleCalendarIcalUrl { get; set; } = string.Empty;
    public List<CalendarSubscription> CalendarSubscriptions { get; set; } = new();
    public List<CalendarEvent> CustomEvents { get; set; } = new();
    public bool AutoSyncCalendar { get; set; } = true;
    public int CalendarSyncIntervalMinutes { get; set; } = 30;

    // Google Calendar API (OAuth 2.0)
    public string GoogleClientId { get; set; } = string.Empty;
    public string GoogleClientSecret { get; set; } = string.Empty;
    public string GoogleRefreshTokenEncrypted { get; set; } = string.Empty;
    public string GoogleAccountEmail { get; set; } = string.Empty;
    public bool GoogleSyncEnabled { get; set; } = false;

    // iPhone (iCloud CalDAV)
    public string ICloudAppleId { get; set; } = string.Empty;
    public string ICloudAppSpecificPasswordEncrypted { get; set; } = string.Empty;
    public string ICloudCalendarUrl { get; set; } = string.Empty;
    public string ICloudCalendarName { get; set; } = "iCloud カレンダー";
    public bool ICloudSyncEnabled { get; set; } = false;
}
