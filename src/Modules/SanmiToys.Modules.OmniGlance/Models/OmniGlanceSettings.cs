using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SanmiToys.Modules.OmniGlance.Models;

public enum IslandOrientation
{
    Horizontal,
    Vertical
}

public enum IslandColorMode
{
    Color,
    Monochrome
}

/// <summary>
/// 縦・横モード共通の配置スロット。
/// Horizontal: StartStart=左上, CenterStart=上中央, EndStart=右上,
///             StartEnd=左下, CenterEnd=下中央, EndEnd=右下
/// Vertical:   StartStart=左上, StartCenter=左中央, StartEnd=左下,
///             EndStart=右上,   EndCenter=右中央,   EndEnd=右下
/// </summary>
public enum IslandPositionSlot
{
    StartStart,
    CenterStart,
    EndStart,
    StartEnd,
    CenterEnd,
    EndEnd,
    StartCenter, // 縦モードのみ使用
    EndCenter    // 縦モードのみ使用
}

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
    public bool IsEnabled { get; set; } = false;

    // --- 新配置体系 ---
    public IslandOrientation Orientation { get; set; } = IslandOrientation.Horizontal;
    public IslandColorMode ColorMode { get; set; } = IslandColorMode.Color;
    public bool ShowBadgeBackground { get; set; } = false;
    public bool HasMigratedToSlots { get; set; } = false; // 新体系マイグレーション完了フラグ

    // --- 縦横それぞれの配置記憶 ---
    public IslandPositionSlot HorizontalPositionSlot { get; set; } = IslandPositionSlot.CenterStart;
    public bool HorizontalIsCustomPosition { get; set; } = false;
    public double HorizontalCustomLeft { get; set; } = -1;
    public double HorizontalCustomTop { get; set; } = -1;

    public IslandPositionSlot VerticalPositionSlot { get; set; } = IslandPositionSlot.StartCenter;
    public bool VerticalIsCustomPosition { get; set; } = false;
    public double VerticalCustomLeft { get; set; } = -1;
    public double VerticalCustomTop { get; set; } = -1;

    // 現在の Orientation に応じたアクティブプロパティ
    [JsonIgnore]
    public IslandPositionSlot PositionSlot
    {
        get => Orientation == IslandOrientation.Vertical ? VerticalPositionSlot : HorizontalPositionSlot;
        set
        {
            if (Orientation == IslandOrientation.Vertical)
                VerticalPositionSlot = value;
            else
                HorizontalPositionSlot = value;
        }
    }

    [JsonIgnore]
    public bool IsCustomPosition
    {
        get => Orientation == IslandOrientation.Vertical ? VerticalIsCustomPosition : HorizontalIsCustomPosition;
        set
        {
            if (Orientation == IslandOrientation.Vertical)
                VerticalIsCustomPosition = value;
            else
                HorizontalIsCustomPosition = value;
        }
    }

    [JsonIgnore]
    public double CustomLeft
    {
        get => Orientation == IslandOrientation.Vertical ? VerticalCustomLeft : HorizontalCustomLeft;
        set
        {
            if (Orientation == IslandOrientation.Vertical)
                VerticalCustomLeft = value;
            else
                HorizontalCustomLeft = value;
        }
    }

    [JsonIgnore]
    public double CustomTop
    {
        get => Orientation == IslandOrientation.Vertical ? VerticalCustomTop : HorizontalCustomTop;
        set
        {
            if (Orientation == IslandOrientation.Vertical)
                VerticalCustomTop = value;
            else
                HorizontalCustomTop = value;
        }
    }

    // --- 旧配置体系（後方互換用） ---
    [JsonPropertyName("PositionSlot")]
    public IslandPositionSlot? LegacyPositionSlot
    {
        get => null;
        set
        {
            if (value.HasValue && !HasMigratedToSlots)
            {
                if (Orientation == IslandOrientation.Vertical)
                    VerticalPositionSlot = value.Value;
                else
                    HorizontalPositionSlot = value.Value;
            }
        }
    }

    [Obsolete("Use Orientation + PositionSlot instead")]
    public IslandPositionMode PositionMode { get; set; } = IslandPositionMode.TopCenter;

    public bool IsPositionLocked { get; set; } = false;
    public bool AllowTaskbarPlacement { get; set; } = false;
    public bool IsClickThrough { get; set; } = false;
    public bool AutoExpandOnHover { get; set; } = false;
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
    public bool EnablePulseAnimation { get; set; } = false;
    public bool EnableDemoDevices { get; set; } = false;

    // パフォーマンス警告設定
    public bool EnableCpuTempAlert { get; set; } = true;
    public int CpuTempAlertThreshold { get; set; } = 80;
    public bool EnableGpuTempAlert { get; set; } = true;
    public int GpuTempAlertThreshold { get; set; } = 80;
    public bool EnableMemoryAlert { get; set; } = true;
    public int MemoryAlertThreshold { get; set; } = 85;

    public double IslandScale { get; set; } = 1.2;
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
