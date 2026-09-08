using System;
using System.Collections.Generic;
using Microsoft.Win32;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services;

public static class AdditionalClocksService
{
    private const string REG_BASE_PATH = @"Control Panel\TimeDate\AdditionalClocks";

    public static List<WorldClockInfo> LoadConfiguredClocks()
    {
        var list = new List<WorldClockInfo>();

        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(REG_BASE_PATH);
            if (baseKey == null) return list;

            string[] subKeys = { "1", "2" };
            foreach (var subName in subKeys)
            {
                using var subKey = baseKey.OpenSubKey(subName);
                if (subKey == null) continue;

                object? enableVal = subKey.GetValue("Enable");
                int enabled = 0;
                if (enableVal is int i) enabled = i;
                else if (enableVal is string s && int.TryParse(s, out int parsed)) enabled = parsed;

                if (enabled != 1) continue;

                string tzRegName = subKey.GetValue("TzRegKeyName") as string ?? string.Empty;
                string displayName = subKey.GetValue("DisplayName") as string ?? string.Empty;

                if (string.IsNullOrWhiteSpace(tzRegName)) continue;

                TimeZoneInfo? tz = null;
                try
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById(tzRegName);
                }
                catch { }

                if (tz == null) continue;

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = tz.DisplayName;
                }

                list.Add(new WorldClockInfo
                {
                    DisplayName = displayName,
                    TimeZoneId = tzRegName,
                    TimeZone = tz
                });
            }
        }
        catch { }

        UpdateClockTimes(list);
        return list;
    }

    public static void UpdateClockTimes(IEnumerable<WorldClockInfo> clocks)
    {
        var utcNow = DateTime.UtcNow;
        var localNow = DateTime.Now;

        foreach (var clock in clocks)
        {
            if (clock.TimeZone == null) continue;

            DateTime tzTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, clock.TimeZone);
            clock.TimeText = tzTime.ToString("HH:mm");

            TimeSpan diff = clock.TimeZone.GetUtcOffset(utcNow) - TimeZoneInfo.Local.GetUtcOffset(utcNow);
            double diffHours = diff.TotalHours;
            string diffSign = diffHours >= 0 ? "+" : "";

            string dayNote = "";
            if (tzTime.Date > localNow.Date) dayNote = "明日 ";
            else if (tzTime.Date < localNow.Date) dayNote = "昨日 ";

            clock.DayDiffText = $"{dayNote}({diffSign}{diffHours:0.#}h)";
        }
    }
}
