using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services;

public class ICloudCalendarInfo
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#4CC2FF";
}

public class ICloudCalDavClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private const string BaseServerUrl = "https://caldav.icloud.com";

    public ICloudCalDavClient(string appleId, string appSpecificPassword)
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SanmiToys-OmniGlance/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(25);

        if (!string.IsNullOrWhiteSpace(appleId) && !string.IsNullOrWhiteSpace(appSpecificPassword))
        {
            var authBytes = Encoding.UTF8.GetBytes($"{appleId.Trim()}:{appSpecificPassword.Trim()}");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }
    }

    /// <summary>
    /// iCloud CalDAV 接続をテストし、利用可能なカレンダー一覧を取得します。
    /// </summary>
    public async Task<(bool success, string message, List<ICloudCalendarInfo> calendars)> TestConnectionAsync()
    {
        try
        {
            string? homeUrl = await DiscoverCalendarHomeUrlAsync();
            if (string.IsNullOrWhiteSpace(homeUrl))
            {
                return (false, "iCloud 認証に失敗しました。Apple ID または App用パスワードを確認してください。", new List<ICloudCalendarInfo>());
            }

            var calendars = await DiscoverCalendarsAsync(homeUrl);
            if (calendars.Count == 0)
            {
                // ホームコレクション自体をデフォルトカレンダーとして登録
                calendars.Add(new ICloudCalendarInfo
                {
                    Name = "iCloud カレンダー",
                    Url = homeUrl,
                    ColorHex = "#4CC2FF"
                });
            }

            return (true, $"接続成功: {calendars.Count}件のカレンダーを検出しました。", calendars);
        }
        catch (Exception ex)
        {
            return (false, $"接続エラー: {ex.Message}", new List<ICloudCalendarInfo>());
        }
    }

    /// <summary>
    /// CalDAV サーバーからユーザーの Principal URL およびカレンダーホームコレクション URL を自動検出します。
    /// </summary>
    public async Task<string?> DiscoverCalendarHomeUrlAsync()
    {
        try
        {
            // 1. Current user principal を取得
            var propfindReq = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"{BaseServerUrl}/")
            {
                Content = new StringContent(
                    @"<?xml version=""1.0"" encoding=""utf-8"" ?>
<D:propfind xmlns:D=""DAV:"">
  <D:prop>
    <D:current-user-principal />
  </D:prop>
</D:propfind>",
                    Encoding.UTF8,
                    "application/xml")
            };
            propfindReq.Headers.Add("Depth", "0");

            var res = await _httpClient.SendAsync(propfindReq);
            if (!res.IsSuccessStatusCode) return null;

            var xmlContent = await res.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xmlContent);
            XNamespace davNs = "DAV:";
            var principalElem = doc.Descendants(davNs + "current-user-principal").Descendants(davNs + "href").FirstOrDefault();
            if (principalElem == null || string.IsNullOrWhiteSpace(principalElem.Value)) return null;

            string principalUrl = principalElem.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? principalElem.Value
                : $"{BaseServerUrl}{principalElem.Value}";

            // 2. Calendar Home Set を取得
            var homeReq = new HttpRequestMessage(new HttpMethod("PROPFIND"), principalUrl)
            {
                Content = new StringContent(
                    @"<?xml version=""1.0"" encoding=""utf-8"" ?>
<D:propfind xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
  <D:prop>
    <C:calendar-home-set />
  </D:prop>
</D:propfind>",
                    Encoding.UTF8,
                    "application/xml")
            };
            homeReq.Headers.Add("Depth", "0");

            var homeRes = await _httpClient.SendAsync(homeReq);
            if (!homeRes.IsSuccessStatusCode) return null;

            var homeXml = await homeRes.Content.ReadAsStringAsync();
            var homeDoc = XDocument.Parse(homeXml);
            XNamespace caldavNs = "urn:ietf:params:xml:ns:caldav";
            var homeElem = homeDoc.Descendants(caldavNs + "calendar-home-set").Descendants(davNs + "href").FirstOrDefault();
            if (homeElem == null || string.IsNullOrWhiteSpace(homeElem.Value)) return null;

            return homeElem.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? homeElem.Value
                : $"{BaseServerUrl}{homeElem.Value}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// カレンダーホームコレクション内の個別カレンダー一覧を取得します。
    /// </summary>
    public async Task<List<ICloudCalendarInfo>> DiscoverCalendarsAsync(string homeUrl)
    {
        var result = new List<ICloudCalendarInfo>();
        try
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), homeUrl)
            {
                Content = new StringContent(
                    @"<?xml version=""1.0"" encoding=""utf-8"" ?>
<D:propfind xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"" xmlns:IC=""http://apple.com/ns/ical/"">
  <D:prop>
    <D:displayname />
    <D:resourcetype />
    <IC:calendar-color />
  </D:prop>
</D:propfind>",
                    Encoding.UTF8,
                    "application/xml")
            };
            req.Headers.Add("Depth", "1");

            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return result;

            string xml = await res.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            XNamespace dav = "DAV:";
            XNamespace caldav = "urn:ietf:params:xml:ns:caldav";
            XNamespace ical = "http://apple.com/ns/ical/";

            foreach (var resp in doc.Descendants(dav + "response"))
            {
                var href = resp.Descendants(dav + "href").FirstOrDefault()?.Value;
                if (string.IsNullOrWhiteSpace(href)) continue;

                var isCalendar = resp.Descendants(caldav + "calendar").Any();
                if (!isCalendar) continue;

                string name = resp.Descendants(dav + "displayname").FirstOrDefault()?.Value ?? "カレンダー";
                string color = resp.Descendants(ical + "calendar-color").FirstOrDefault()?.Value ?? "#4CC2FF";

                string fullUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? href
                    : $"{BaseServerUrl}{href}";

                result.Add(new ICloudCalendarInfo
                {
                    Name = name,
                    Url = fullUrl,
                    ColorHex = color
                });
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// iCloud カレンダーに予定を作成または更新（PUT）します。
    /// </summary>
    public async Task<bool> UploadEventAsync(string calendarUrl, CalendarEvent ev)
    {
        if (string.IsNullOrWhiteSpace(calendarUrl)) return false;

        try
        {
            string uid = !string.IsNullOrWhiteSpace(ev.ExternalUid) ? ev.ExternalUid : ev.Id;
            if (string.IsNullOrWhiteSpace(uid)) uid = Guid.NewGuid().ToString();

            string eventUrl = $"{calendarUrl.TrimEnd('/')}/{uid}.ics";
            if (!eventUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                eventUrl = $"{BaseServerUrl}{eventUrl}";
            }

            string icsContent = GenerateIcsString(ev, uid);

            var putReq = new HttpRequestMessage(HttpMethod.Put, eventUrl)
            {
                Content = new StringContent(icsContent, Encoding.UTF8, "text/calendar")
            };

            var res = await _httpClient.SendAsync(putReq);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// iCloud カレンダーから予定を削除（DELETE）します。
    /// </summary>
    public async Task<bool> DeleteEventAsync(string calendarUrl, string uid)
    {
        if (string.IsNullOrWhiteSpace(calendarUrl) || string.IsNullOrWhiteSpace(uid)) return false;

        try
        {
            string eventUrl = $"{calendarUrl.TrimEnd('/')}/{uid}.ics";
            if (!eventUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                eventUrl = $"{BaseServerUrl}{eventUrl}";
            }

            var delReq = new HttpRequestMessage(HttpMethod.Delete, eventUrl);
            var res = await _httpClient.SendAsync(delReq);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// CalDAV REPORT (calendar-query) を用いて指定期間のイベント（ICS 文字列一覧）を取得します。
    /// </summary>
    public async Task<List<string>> GetEventIcsListAsync(string calendarUrl, DateTime start, DateTime end)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(calendarUrl)) return result;

        string queryXml = $@"<?xml version=""1.0"" encoding=""utf-8"" ?>
<C:calendar-query xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
  <D:prop>
    <D:getetag />
    <C:calendar-data />
  </D:prop>
  <C:filter>
    <C:comp-filter name=""VCALENDAR"">
      <C:comp-filter name=""VEVENT"">
        <C:time-range start=""{start.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}"" end=""{end.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}"" />
      </C:comp-filter>
    </C:comp-filter>
  </C:filter>
</C:calendar-query>";

        try
        {
            string reqUrl = calendarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? calendarUrl
                : $"{BaseServerUrl}{calendarUrl}";

            var req = new HttpRequestMessage(new HttpMethod("REPORT"), reqUrl)
            {
                Content = new StringContent(queryXml, Encoding.UTF8, "application/xml")
            };
            req.Headers.Add("Depth", "1");

            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return result;

            string xml = await res.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            XNamespace caldav = "urn:ietf:params:xml:ns:caldav";

            foreach (var elem in doc.Descendants(caldav + "calendar-data"))
            {
                string ics = elem.Value;
                if (!string.IsNullOrWhiteSpace(ics))
                {
                    result.Add(ics);
                }
            }
        }
        catch { }

        return result;
    }

    private static string GenerateIcsString(CalendarEvent ev, string uid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//SanmiToys//OmniGlance//JA");
        sb.AppendLine("CALSCALE:GREGORIAN");
        sb.AppendLine("BEGIN:VEVENT");
        sb.AppendLine($"UID:{uid}");
        sb.AppendLine($"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}");

        if (ev.IsAllDay)
        {
            sb.AppendLine($"DTSTART;VALUE=DATE:{ev.StartTime:yyyyMMdd}");
            var endDay = ev.EndTime > ev.StartTime ? ev.EndTime : ev.StartTime.AddDays(1);
            sb.AppendLine($"DTEND;VALUE=DATE:{endDay:yyyyMMdd}");
        }
        else
        {
            sb.AppendLine($"DTSTART:{ev.StartTime.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}");
            sb.AppendLine($"DTEND:{ev.EndTime.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}");
        }

        sb.AppendLine($"SUMMARY:{EscapeIcs(ev.Title)}");
        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            sb.AppendLine($"LOCATION:{EscapeIcs(ev.Location)}");
        }
        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            sb.AppendLine($"DESCRIPTION:{EscapeIcs(ev.Description)}");
        }

        sb.AppendLine("END:VEVENT");
        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    private static string EscapeIcs(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\")
                .Replace(";", "\\;")
                .Replace(",", "\\,")
                .Replace("\n", "\\n")
                .Replace("\r", "");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
