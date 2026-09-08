using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services;

public class GoogleCalendarApiClient
{
    private const string BaseApiUrl = "https://www.googleapis.com/calendar/v3/calendars/primary/events";
    private readonly GoogleOAuthService _authService;
    private readonly HttpClient _httpClient;

    public GoogleCalendarApiClient(GoogleOAuthService authService)
    {
        _authService = authService;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Google カレンダーから指定期間の予定一覧を取得します。
    /// </summary>
    public async Task<List<CalendarEvent>> GetEventsAsync(DateTime minDate, DateTime maxDate)
    {
        string? token = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return new List<CalendarEvent>();

        string timeMin = minDate.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        string timeMax = maxDate.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        string url = $"{BaseApiUrl}?timeMin={Uri.EscapeDataString(timeMin)}&timeMax={Uri.EscapeDataString(timeMax)}&singleEvents=true&orderBy=startTime&maxResults=250";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return new List<CalendarEvent>();

            string json = await res.Content.ReadAsStringAsync();
            return ParseGoogleEventsJson(json);
        }
        catch
        {
            return new List<CalendarEvent>();
        }
    }

    /// <summary>
    /// Google カレンダーに新しい予定を作成します。
    /// </summary>
    public async Task<string?> InsertEventAsync(CalendarEvent ev)
    {
        string? token = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        var bodyObj = BuildEventJsonObject(ev);
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseApiUrl)
        {
            Content = new StringContent(bodyObj.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Google カレンダーの既存予定を更新します。
    /// </summary>
    public async Task<bool> UpdateEventAsync(CalendarEvent ev)
    {
        string? token = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        string eventId = !string.IsNullOrWhiteSpace(ev.ExternalUid) ? ev.ExternalUid : ev.Id;
        if (string.IsNullOrWhiteSpace(eventId)) return false;

        string url = $"{BaseApiUrl}/{Uri.EscapeDataString(eventId)}";
        var bodyObj = BuildEventJsonObject(ev);

        using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(bodyObj.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var res = await _httpClient.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Google カレンダーから予定を削除します。
    /// </summary>
    public async Task<bool> DeleteEventAsync(string eventId)
    {
        string? token = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token) || string.IsNullOrWhiteSpace(eventId)) return false;

        string url = $"{BaseApiUrl}/{Uri.EscapeDataString(eventId)}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var res = await _httpClient.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static JsonObject BuildEventJsonObject(CalendarEvent ev)
    {
        var obj = new JsonObject
        {
            ["summary"] = ev.Title,
            ["description"] = ev.Description ?? string.Empty,
            ["location"] = ev.Location ?? string.Empty
        };

        if (ev.IsAllDay)
        {
            obj["start"] = new JsonObject { ["date"] = ev.StartTime.ToString("yyyy-MM-dd") };
            var endDay = ev.EndTime > ev.StartTime ? ev.EndTime : ev.StartTime.AddDays(1);
            obj["end"] = new JsonObject { ["date"] = endDay.ToString("yyyy-MM-dd") };
        }
        else
        {
            obj["start"] = new JsonObject { ["dateTime"] = ev.StartTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz") };
            obj["end"] = new JsonObject { ["dateTime"] = ev.EndTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz") };
        }

        return obj;
    }

    private static List<CalendarEvent> ParseGoogleEventsJson(string json)
    {
        var list = new List<CalendarEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in items.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                string summary = item.TryGetProperty("summary", out var sumProp) ? sumProp.GetString() ?? "(無題)" : "(無題)";
                string location = item.TryGetProperty("location", out var locProp) ? locProp.GetString() ?? "" : "";
                string description = item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
                string htmlLink = item.TryGetProperty("htmlLink", out var linkProp) ? linkProp.GetString() ?? "" : "";

                DateTime start = DateTime.Now;
                DateTime end = DateTime.Now.AddHours(1);
                bool isAllDay = false;

                if (item.TryGetProperty("start", out var startProp))
                {
                    if (startProp.TryGetProperty("date", out var dProp) && DateTime.TryParse(dProp.GetString(), out var dVal))
                    {
                        start = dVal;
                        isAllDay = true;
                    }
                    else if (startProp.TryGetProperty("dateTime", out var dtProp) && DateTime.TryParse(dtProp.GetString(), out var dtVal))
                    {
                        start = dtVal;
                    }
                }

                if (item.TryGetProperty("end", out var endProp))
                {
                    if (endProp.TryGetProperty("date", out var dProp) && DateTime.TryParse(dProp.GetString(), out var dVal))
                    {
                        end = dVal;
                    }
                    else if (endProp.TryGetProperty("dateTime", out var dtProp) && DateTime.TryParse(dtProp.GetString(), out var dtVal))
                    {
                        end = dtVal;
                    }
                }

                list.Add(new CalendarEvent
                {
                    Id = id,
                    ExternalUid = id,
                    CalendarId = "google",
                    CalendarName = "Google カレンダー",
                    ProviderType = CalendarProviderType.GoogleCalendar,
                    Title = summary,
                    StartTime = start,
                    EndTime = end,
                    IsAllDay = isAllDay,
                    Location = location,
                    Description = description,
                    Url = htmlLink,
                    ColorHex = "#4CC2FF"
                });
            }
        }
        catch { }

        return list;
    }
}
