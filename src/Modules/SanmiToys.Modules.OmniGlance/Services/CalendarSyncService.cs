using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services;

public class CalendarSyncService : IDisposable
{
    private readonly Func<OmniGlanceSettings> _getSettings;
    private readonly Action<OmniGlanceSettings>? _saveSettings;
    private readonly HttpClient _httpClient;
    private System.Threading.Timer? _syncTimer;
    private bool _isSyncing;

    public GoogleOAuthService GoogleAuth { get; }
    public GoogleCalendarApiClient GoogleApi { get; }

    public ObservableCollection<CalendarEvent> Events { get; } = new();
    public DateTime? LastSyncTime { get; private set; }
    public string LastSyncStatus { get; private set; } = "未同期";

    public event Action? CalendarUpdated;

    public CalendarSyncService(Func<OmniGlanceSettings> getSettings, Action<OmniGlanceSettings>? saveSettings = null)
    {
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SanmiToys-OmniGlance/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(20);

        GoogleAuth = new GoogleOAuthService(_getSettings, s => _saveSettings?.Invoke(s));
        GoogleApi = new GoogleCalendarApiClient(GoogleAuth);
    }

    public void Start()
    {
        Stop();
        var s = _getSettings();
        int intervalMs = Math.Max(5, s.CalendarSyncIntervalMinutes) * 60 * 1000;
        _syncTimer = new System.Threading.Timer(async _ => await SyncAsync(), null, 1000, intervalMs);
    }

    public void Stop()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
    }

    public async Task SyncAsync()
    {
        if (_isSyncing) return;
        _isSyncing = true;

        var settings = _getSettings();
        var allEvents = new List<CalendarEvent>();
        var errors = new List<string>();
        int successCount = 0;

        // 1. Google Calendar 連携
        if (settings.GoogleSyncEnabled)
        {
            bool syncedGoogle = false;
            // 1-A: Google Calendar API (REST + OAuth 2.0)
            if (GoogleAuth.IsSignedIn)
            {
                try
                {
                    var today = DateTime.Today;
                    var gEvents = await GoogleApi.GetEventsAsync(today.AddMonths(-1), today.AddMonths(2));
                    allEvents.AddRange(gEvents);
                    successCount++;
                    syncedGoogle = true;
                }
                catch (Exception ex)
                {
                    errors.Add($"Google API: {ex.Message}");
                }
            }

            // 1-B: または Google iCal 非公開 URL (手軽連携 / OAuth未設定時)
            if (!syncedGoogle && !string.IsNullOrWhiteSpace(settings.GoogleCalendarIcalUrl))
            {
                try
                {
                    string ics = await _httpClient.GetStringAsync(settings.GoogleCalendarIcalUrl.Trim());
                    var gEvents = ParseIcs(ics);
                    foreach (var ev in gEvents)
                    {
                        ev.CalendarId = "google";
                        ev.CalendarName = "Google カレンダー";
                        ev.ProviderType = CalendarProviderType.GoogleCalendar;
                        ev.ColorHex = "#4CC2FF";
                        allEvents.Add(ev);
                    }
                    successCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Google iCal: {ex.Message}");
                }
            }
        }

        // 2. iPhone (iCloud CalDAV) 連携
        if (settings.ICloudSyncEnabled && !string.IsNullOrWhiteSpace(settings.ICloudAppleId))
        {
            try
            {
                string appPw = SecurityHelper.DecryptString(settings.ICloudAppSpecificPasswordEncrypted);
                if (!string.IsNullOrWhiteSpace(appPw))
                {
                    using var iCloudClient = new ICloudCalDavClient(settings.ICloudAppleId, appPw);
                    string? calUrl = settings.ICloudCalendarUrl;
                    if (string.IsNullOrWhiteSpace(calUrl))
                    {
                        var (_, _, calendars) = await iCloudClient.TestConnectionAsync();
                        if (calendars.Count > 0)
                        {
                            calUrl = calendars[0].Url;
                            settings.ICloudCalendarUrl = calUrl;
                            settings.ICloudCalendarName = calendars[0].Name;
                            _saveSettings?.Invoke(settings);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(calUrl))
                    {
                        var today = DateTime.Today;
                        var icsList = await iCloudClient.GetEventIcsListAsync(calUrl, today.AddMonths(-1), today.AddMonths(2));
                        foreach (var ics in icsList)
                        {
                            var icloudEvents = ParseIcs(ics);
                            foreach (var ev in icloudEvents)
                            {
                                ev.CalendarId = "icloud";
                                ev.CalendarName = settings.ICloudCalendarName;
                                ev.ProviderType = CalendarProviderType.AppleICloud;
                                ev.ColorHex = "#52C41A";
                                allEvents.Add(ev);
                            }
                        }
                        successCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"iCloud: {ex.Message}");
            }
        }

        // 3. 一般 iCal URL 購読
        var activeSubs = settings.CalendarSubscriptions.Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (activeSubs.Count > 0)
        {
            foreach (var sub in activeSubs)
            {
                string url = sub.Url.Trim();
                if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url.Substring(9);
                }

                CalendarProviderType provider = sub.ProviderType;
                if (provider == CalendarProviderType.GenericIcal)
                {
                    if (url.Contains("google.com", StringComparison.OrdinalIgnoreCase))
                        provider = CalendarProviderType.GoogleCalendar;
                    else if (url.Contains("icloud.com", StringComparison.OrdinalIgnoreCase))
                        provider = CalendarProviderType.AppleICloud;
                }

                try
                {
                    string icsContent = await _httpClient.GetStringAsync(url);
                    var subEvents = ParseIcs(icsContent);
                    foreach (var ev in subEvents)
                    {
                        ev.CalendarId = sub.Id;
                        ev.CalendarName = sub.Name;
                        ev.ColorHex = sub.ColorHex;
                        ev.ProviderType = provider;
                        allEvents.Add(ev);
                    }
                    successCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{sub.Name}: {ex.Message}");
                }
            }
        }

        // 4. ローカル予定 (CustomEvents) の統合
        if (settings.CustomEvents != null && settings.CustomEvents.Count > 0)
        {
            foreach (var cev in settings.CustomEvents)
            {
                cev.CalendarId = "local";
                cev.ProviderType = CalendarProviderType.Local;
                if (string.IsNullOrWhiteSpace(cev.CalendarName))
                {
                    cev.CalendarName = "マイカレンダー";
                }
                allEvents.Add(cev);
            }
        }

        // URLもアカウントも未設定時のデモサンプル
        if (allEvents.Count == 0 && !settings.GoogleSyncEnabled && !settings.ICloudSyncEnabled && activeSubs.Count == 0 && (settings.CustomEvents == null || settings.CustomEvents.Count == 0))
        {
            LastSyncStatus = "未設定 (サンプル表示中)";
            allEvents = GenerateSampleEvents();
        }
        else
        {
            LastSyncTime = DateTime.Now;
            if (errors.Count == 0)
            {
                LastSyncStatus = $"同期成功 ({allEvents.Count}件)";
            }
            else
            {
                LastSyncStatus = $"一部失敗 ({errors.Count}件エラー): {string.Join(", ", errors)}";
            }
        }

        // UI スレッドに反映
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Events.Clear();
            foreach (var ev in allEvents.OrderBy(e => e.StartTime))
            {
                Events.Add(ev);
            }
            CalendarUpdated?.Invoke();
        });

        _isSyncing = false;
    }

    public async Task<bool> SaveEventAsync(CalendarEvent ev)
    {
        var settings = _getSettings();

        // 1. Google Calendar API (REST v3)
        if (ev.ProviderType == CalendarProviderType.GoogleCalendar && settings.GoogleSyncEnabled && GoogleAuth.IsSignedIn)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ev.ExternalUid))
                {
                    string? newId = await GoogleApi.InsertEventAsync(ev);
                    if (!string.IsNullOrEmpty(newId))
                    {
                        ev.Id = newId;
                        ev.ExternalUid = newId;
                        AddEventToObservableCollection(ev);
                        return true;
                    }
                }
                else
                {
                    bool ok = await GoogleApi.UpdateEventAsync(ev);
                    if (ok)
                    {
                        UpdateEventInObservableCollection(ev);
                        return true;
                    }
                }
            }
            catch { }
        }

        // 2. iPhone (iCloud CalDAV)
        if (ev.ProviderType == CalendarProviderType.AppleICloud && settings.ICloudSyncEnabled && !string.IsNullOrWhiteSpace(settings.ICloudAppleId))
        {
            try
            {
                string appPw = SecurityHelper.DecryptString(settings.ICloudAppSpecificPasswordEncrypted);
                if (!string.IsNullOrWhiteSpace(appPw))
                {
                    using var iCloudClient = new ICloudCalDavClient(settings.ICloudAppleId, appPw);
                    string? calUrl = settings.ICloudCalendarUrl;
                    if (string.IsNullOrWhiteSpace(calUrl))
                    {
                        var (_, _, calendars) = await iCloudClient.TestConnectionAsync();
                        if (calendars.Count > 0)
                        {
                            calUrl = calendars[0].Url;
                            settings.ICloudCalendarUrl = calUrl;
                            _saveSettings?.Invoke(settings);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(calUrl))
                    {
                        bool ok = await iCloudClient.UploadEventAsync(calUrl, ev);
                        if (ok)
                        {
                            UpdateOrAddEventInCollection(ev);
                            return true;
                        }
                    }
                }
            }
            catch { }
        }

        // 3. ローカルカレンダーとして保存
        ev.CalendarId = "local";
        ev.ProviderType = CalendarProviderType.Local;
        var existing = settings.CustomEvents.FirstOrDefault(e => e.Id == ev.Id);
        if (existing != null)
        {
            existing.Title = ev.Title;
            existing.StartTime = ev.StartTime;
            existing.EndTime = ev.EndTime;
            existing.IsAllDay = ev.IsAllDay;
            existing.Location = ev.Location;
            existing.ColorHex = ev.ColorHex;
            existing.Description = ev.Description;
            UpdateEventInObservableCollection(ev);
        }
        else
        {
            settings.CustomEvents.Add(ev);
            AddEventToObservableCollection(ev);
        }
        _saveSettings?.Invoke(settings);
        CalendarUpdated?.Invoke();
        return true;
    }

    public async Task<bool> DeleteEventAsync(CalendarEvent ev)
    {
        var settings = _getSettings();

        // 1. Google API
        if (ev.ProviderType == CalendarProviderType.GoogleCalendar && settings.GoogleSyncEnabled && GoogleAuth.IsSignedIn)
        {
            try
            {
                string eventId = !string.IsNullOrWhiteSpace(ev.ExternalUid) ? ev.ExternalUid : ev.Id;
                await GoogleApi.DeleteEventAsync(eventId);
            }
            catch { }
        }

        // 2. iCloud CalDAV
        if (ev.ProviderType == CalendarProviderType.AppleICloud && settings.ICloudSyncEnabled && !string.IsNullOrWhiteSpace(settings.ICloudAppleId))
        {
            try
            {
                string appPw = SecurityHelper.DecryptString(settings.ICloudAppSpecificPasswordEncrypted);
                if (!string.IsNullOrWhiteSpace(appPw) && !string.IsNullOrWhiteSpace(settings.ICloudCalendarUrl))
                {
                    using var iCloudClient = new ICloudCalDavClient(settings.ICloudAppleId, appPw);
                    string uid = !string.IsNullOrWhiteSpace(ev.ExternalUid) ? ev.ExternalUid : ev.Id;
                    await iCloudClient.DeleteEventAsync(settings.ICloudCalendarUrl, uid);
                }
            }
            catch { }
        }

        // 3. ローカル
        settings.CustomEvents.RemoveAll(e => e.Id == ev.Id);
        _saveSettings?.Invoke(settings);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var target = Events.FirstOrDefault(e => e.Id == ev.Id || (!string.IsNullOrEmpty(e.ExternalUid) && e.ExternalUid == ev.ExternalUid));
            if (target != null) Events.Remove(target);
            CalendarUpdated?.Invoke();
        });

        return true;
    }

    public void AddCustomEvent(CalendarEvent ev)
    {
        _ = SaveEventAsync(ev);
    }

    public void DeleteCustomEvent(string eventId)
    {
        var ev = Events.FirstOrDefault(e => e.Id == eventId);
        if (ev != null)
        {
            _ = DeleteEventAsync(ev);
        }
        else
        {
            var settings = _getSettings();
            settings.CustomEvents.RemoveAll(e => e.Id == eventId);
            _saveSettings?.Invoke(settings);
            CalendarUpdated?.Invoke();
        }
    }

    private void AddEventToObservableCollection(CalendarEvent ev)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            int index = 0;
            while (index < Events.Count && Events[index].StartTime <= ev.StartTime)
            {
                index++;
            }
            Events.Insert(index, ev);
            CalendarUpdated?.Invoke();
        });
    }

    private void UpdateEventInObservableCollection(CalendarEvent ev)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = Events.FirstOrDefault(e => e.Id == ev.Id || (!string.IsNullOrEmpty(e.ExternalUid) && e.ExternalUid == ev.ExternalUid));
            if (existing != null) Events.Remove(existing);

            int index = 0;
            while (index < Events.Count && Events[index].StartTime <= ev.StartTime)
            {
                index++;
            }
            Events.Insert(index, ev);
            CalendarUpdated?.Invoke();
        });
    }

    private void UpdateOrAddEventInCollection(CalendarEvent ev)
    {
        UpdateEventInObservableCollection(ev);
    }

    public List<CalendarEvent> GetEventsForDate(DateTime date)
    {
        return Events
            .Where(e => IsEventOnDate(e, date))
            .OrderBy(e => e.IsAllDay ? 0 : 1)
            .ThenBy(e => e.StartTime)
            .ToList();
    }

    public bool HasEventsOnDate(DateTime date)
    {
        return Events.Any(e => IsEventOnDate(e, date));
    }

    public static bool IsEventOnDate(CalendarEvent ev, DateTime targetDate)
    {
        var dayStart = targetDate.Date;
        var dayEnd = dayStart.AddDays(1);

        var start = ev.StartTime;
        var end = ev.EndTime;

        if (end <= start)
        {
            if (ev.IsAllDay)
            {
                end = start.AddDays(1);
            }
            else
            {
                return start >= dayStart && start < dayEnd;
            }
        }

        return start < dayEnd && end > dayStart;
    }

    public List<Brush> GetDistinctColorsForDate(DateTime date)
    {
        var evs = GetEventsForDate(date);
        var brushes = new List<Brush>();
        var seenColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in evs)
        {
            string hex = ev.ColorHex ?? "#4CC2FF";
            if (seenColors.Add(hex))
            {
                brushes.Add(ev.ColorBrush);
            }
        }

        return brushes;
    }

    private static List<CalendarEvent> ParseIcs(string icsText)
    {
        var result = new List<CalendarEvent>();
        using var reader = new StringReader(icsText);

        string? line;
        CalendarEvent? current = null;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line == "BEGIN:VEVENT")
            {
                current = new CalendarEvent();
            }
            else if (line == "END:VEVENT" && current != null)
            {
                if (!string.IsNullOrWhiteSpace(current.Title))
                {
                    if (current.IsAllDay)
                    {
                        if (current.EndTime <= current.StartTime)
                        {
                            current.EndTime = current.StartTime.AddDays(1);
                        }
                    }
                    else if (current.EndTime < current.StartTime)
                    {
                        current.EndTime = current.StartTime;
                    }
                    result.Add(current);
                }
                current = null;
            }
            else if (current != null)
            {
                if (line.StartsWith("UID:", StringComparison.OrdinalIgnoreCase))
                {
                    current.ExternalUid = line.Substring(4).Trim();
                    if (string.IsNullOrWhiteSpace(current.Id))
                    {
                        current.Id = current.ExternalUid;
                    }
                }
                else if (line.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
                {
                    current.Url = line.Substring(4).Trim();
                }
                else if (line.StartsWith("DESCRIPTION:", StringComparison.OrdinalIgnoreCase))
                {
                    current.Description = UnescapeIcs(line.Substring(12));
                }
                else if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
                {
                    current.Title = UnescapeIcs(line.Substring(8));
                }
                else if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                {
                    current.Location = UnescapeIcs(line.Substring(9));
                }
                else if (line.StartsWith("DTSTART", StringComparison.OrdinalIgnoreCase))
                {
                    var (dt, isDateOnly) = ParseIcsDateTimeEx(line);
                    current.StartTime = dt;
                    if (isDateOnly || line.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase))
                    {
                        current.IsAllDay = true;
                    }
                }
                else if (line.StartsWith("DTEND", StringComparison.OrdinalIgnoreCase))
                {
                    var (dt, isDateOnly) = ParseIcsDateTimeEx(line);
                    current.EndTime = dt;
                    if (isDateOnly || line.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase))
                    {
                        current.IsAllDay = true;
                    }
                }
            }
        }

        return result;
    }

    private static (DateTime dt, bool isDateOnly) ParseIcsDateTimeEx(string line)
    {
        int colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return (DateTime.Now, false);

        string val = line.Substring(colonIdx + 1).Trim();

        // 終日フォーマット: yyyyMMdd
        if (val.Length == 8 && DateTime.TryParseExact(val, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dOnly))
        {
            return (dOnly, true);
        }

        // UTC: yyyyMMddTHHmmssZ
        if (val.EndsWith("Z") && DateTime.TryParseExact(val, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dUtc))
        {
            return (dUtc.ToLocalTime(), false);
        }

        // ローカル: yyyyMMddTHHmmss
        if (DateTime.TryParseExact(val, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dLoc))
        {
            return (dLoc, false);
        }

        if (DateTime.TryParse(val, out var fallback))
        {
            return (fallback, false);
        }

        return (DateTime.Now, false);
    }

    private static string UnescapeIcs(string s)
    {
        return s.Replace("\\n", "\n")
                .Replace("\\N", "\n")
                .Replace("\\,", ",")
                .Replace("\\;", ";")
                .Replace("\\\\", "\\");
    }

    private static List<CalendarEvent> GenerateSampleEvents()
    {
        var today = DateTime.Today;
        return new List<CalendarEvent>
        {
            new()
            {
                CalendarName = "仕事",
                ColorHex = "#4CC2FF",
                Title = "チーム定例ミーティング",
                StartTime = today.AddHours(10),
                EndTime = today.AddHours(11).AddMinutes(30),
                Location = "Google Meet",
                IsAllDay = false,
                ProviderType = CalendarProviderType.GoogleCalendar
            },
            new()
            {
                CalendarName = "プロジェクト",
                ColorHex = "#52C41A",
                Title = "デザインレビュー & 仕様確認",
                StartTime = today.AddHours(14),
                EndTime = today.AddHours(15),
                Location = "第2会議室",
                IsAllDay = false,
                ProviderType = CalendarProviderType.AppleICloud
            },
            new()
            {
                CalendarName = "仕事",
                ColorHex = "#4CC2FF",
                Title = "プロジェクト進捗共有",
                StartTime = today.AddHours(17),
                EndTime = today.AddHours(17).AddMinutes(45),
                Location = "Slack / Discord",
                IsAllDay = false,
                ProviderType = CalendarProviderType.GoogleCalendar
            },
            new()
            {
                CalendarName = "プライベート",
                ColorHex = "#FF6B6B",
                Title = "週末リフレッシュ / 定期バックアップ",
                StartTime = today.AddDays(2),
                EndTime = today.AddDays(3),
                Location = "ホームオフィス",
                IsAllDay = true,
                ProviderType = CalendarProviderType.Local
            },
            new()
            {
                CalendarName = "リマインダー",
                ColorHex = "#FFA940",
                Title = "月次請求・契約更新確認",
                StartTime = today.AddDays(1).AddHours(11),
                EndTime = today.AddDays(1).AddHours(12),
                Location = "オンライン",
                IsAllDay = false,
                ProviderType = CalendarProviderType.Local
            }
        };
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}
