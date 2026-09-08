using System;
using System.Diagnostics;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services;

public static class GoogleCalendarService
{
    /// <summary>
    /// Google カレンダーの該当日画面を既定のブラウザで開きます。
    /// </summary>
    public static bool OpenDateInGoogleCalendar(DateTime date)
    {
        try
        {
            string url = $"https://calendar.google.com/calendar/r/day/{date.Year}/{date.Month}/{date.Day}";
            OpenUrl(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 予定の編集または該当日画面を Google カレンダー Web 上で開きます。
    /// </summary>
    public static bool OpenEventInGoogleCalendar(CalendarEvent ev)
    {
        try
        {
            string url;
            if (!string.IsNullOrWhiteSpace(ev.Title))
            {
                // タイトル検索または該当日表示
                url = $"https://calendar.google.com/calendar/r/day/{ev.StartTime.Year}/{ev.StartTime.Month}/{ev.StartTime.Day}";
            }
            else
            {
                url = "https://calendar.google.com/calendar/r";
            }
            OpenUrl(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// iCloud カレンダー Web を既定のブラウザで開きます。
    /// </summary>
    public static bool OpenICloudCalendar()
    {
        try
        {
            OpenUrl("https://www.icloud.com/calendar");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 既定のブラウザで指定された URL を開きます。
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var psi = new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        };
        Process.Start(psi);
    }
}
