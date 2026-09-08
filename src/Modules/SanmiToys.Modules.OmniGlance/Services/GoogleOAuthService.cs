using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SanmiToys.Modules.OmniGlance.Models;

namespace SanmiToys.Modules.OmniGlance.Services;

public class GoogleOAuthService
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string CalendarScope = "https://www.googleapis.com/auth/calendar.events";
    private const int Port = 53841;

    private readonly Func<OmniGlanceSettings> _getSettings;
    private readonly Action<OmniGlanceSettings> _saveSettings;
    private readonly HttpClient _httpClient;

    private string? _cachedAccessToken;
    private DateTime _accessTokenExpiry = DateTime.MinValue;

    public GoogleOAuthService(Func<OmniGlanceSettings> getSettings, Action<OmniGlanceSettings> saveSettings)
    {
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _httpClient = new HttpClient();
    }

    public bool IsSignedIn
    {
        get
        {
            var settings = _getSettings();
            return !string.IsNullOrWhiteSpace(settings.GoogleRefreshTokenEncrypted) && settings.GoogleSyncEnabled;
        }
    }

    // アプリ組み込みのデフォルト OAuth クライアント情報
    private static readonly byte[] EncryptedClientId =
    [
        111,105,99,99,110,109,99,107,106,108,105,99,119,54,47,47,48,43,59,40,49,46,40,62,42,106,63,99,50,108,107,107,110,52,61,40,42,55,60,111,47,61,41,53,105,116,59,42,42,41,116,61,53,53,61,54,63,47,41,63,40,57,53,52,46,63,52,46,116,57,53,55
    ];

    private static readonly byte[] EncryptedClientSecret =
    [
        29,21,25,9,10,2,119,119,22,42,30,104,43,31,27,56,105,32,20,29,50,25,28,111,8,15,10,19,111,15,13,15,15,10,22
    ];

    private static string DecryptBytes(byte[] data)
    {
        var buffer = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            buffer[i] = (byte)(data[i] ^ 0x5A);
        }
        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    public static string DefaultClientId => DecryptBytes(EncryptedClientId);
    public static string DefaultClientSecret => DecryptBytes(EncryptedClientSecret);

    /// <summary>
    /// ローカル HttpListener と PKCE を用いてデスクトップ OAuth 2.0 認可フローを実行します。
    /// クライアント ID が省略された場合はビルトイン情報または設定値を使用します。
    /// </summary>
    public async Task<(bool success, string message)> SignInAsync(string? customClientId = null, string? customClientSecret = null)
    {
        var settings = _getSettings();
        string clientId = !string.IsNullOrWhiteSpace(customClientId)
            ? customClientId.Trim()
            : (!string.IsNullOrWhiteSpace(settings.GoogleClientId) ? settings.GoogleClientId.Trim() : DefaultClientId);

        string clientSecret = !string.IsNullOrWhiteSpace(customClientSecret)
            ? customClientSecret.Trim()
            : (!string.IsNullOrWhiteSpace(settings.GoogleClientSecret) ? settings.GoogleClientSecret.Trim() : DefaultClientSecret);

        // クライアント ID が未指定の場合はエラー
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (false, "OAuth クライアント ID が設定されていません。");
        }

        // PKCE code_verifier と code_challenge の生成
        var verifierBytes = new byte[32];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(verifierBytes);
        }
        string codeVerifier = Convert.ToBase64String(verifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        string codeChallenge = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ローカル HttpListener の起動
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            return (false, $"ローカル認証リスナーの起動に失敗しました (ポート {Port}): {ex.Message}");
        }

        string redirectUri = $"http://127.0.0.1:{Port}/";
        string state = Guid.NewGuid().ToString("N");

        string authUrl = $"{AuthEndpoint}?" +
                         $"client_id={Uri.EscapeDataString(clientId)}" +
                         $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                         $"&response_type=code" +
                         $"&scope={Uri.EscapeDataString(CalendarScope + " email")}" +
                         $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                         $"&code_challenge_method=S256" +
                         $"&access_type=offline" +
                         $"&prompt=consent" +
                         $"&state={state}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            return (false, $"ブラウザの起動に失敗しました: {ex.Message}");
        }

        // 認可コードの受信待機 (最大120秒)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        HttpListenerContext context;
        Task<HttpListenerContext>? listenTask = null;
        try
        {
            listenTask = listener.GetContextAsync();
            var completedTask = await Task.WhenAny(listenTask, Task.Delay(-1, cts.Token));
            if (completedTask != listenTask)
            {
                try { listener.Stop(); } catch { }
                if (listenTask != null)
                {
                    _ = listenTask.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
                return (false, "認証がタイムアウトしました。Google 画面での操作が中断された可能性があります。");
            }
            context = await listenTask;
        }
        catch (Exception ex)
        {
            try { listener.Stop(); } catch { }
            if (listenTask != null)
            {
                _ = listenTask.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            return (false, $"認証の受信でエラーが発生しました: {ex.Message}");
        }

        var req = context.Request;
        var res = context.Response;

        string? code = req.QueryString["code"];
        string? error = req.QueryString["error"];

        // ブラウザへの応答 HTML
        string responseHtml = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>SanmiToys</title>
<style>body{font-family:Segoe UI,sans-serif;background:#0F1015;color:#FFF;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}
.card{background:#1B1C22;border:1px solid #333;border-radius:12px;padding:32px 48px;text-align:center;box-shadow:0 8px 32px rgba(0,0,0,0.5);}
h2{color:#4CC2FF;margin-top:0;}p{color:#AAA;font-size:14px;}</style></head>
<body><div class='card'><h2>SanmiToys カレンダー連携</h2><p>Google アカウントの認証が完了しました。<br>このウィンドウを閉じてアプリに戻ってください。</p></div></body></html>";

        byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
        res.ContentLength64 = buffer.Length;
        res.ContentType = "text/html; charset=utf-8";
        try
        {
            await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            res.OutputStream.Close();
        }
        catch { }
        finally
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            return (false, $"認証が拒否されました: {error ?? "コード取得失敗"}");
        }

        // 認可コードをトークンに交換 (PKCE code_verifier 送信)
        var tokenParams = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            tokenParams["client_secret"] = clientSecret;
        }

        try
        {
            var tokenRes = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(tokenParams));
            string json = await tokenRes.Content.ReadAsStringAsync();
            if (!tokenRes.IsSuccessStatusCode)
            {
                if (json.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "Google OAuth クライアントが無効です。[詳細設定] から有効なクライアント ID を入力するか、iCal 非公開 URL をご利用ください。");
                }
                return (false, $"トークン取得エラー: {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string accessToken = root.GetProperty("access_token").GetString() ?? "";
            string? refreshToken = root.TryGetProperty("refresh_token", out var rfProp) ? rfProp.GetString() : null;
            int expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;

            _cachedAccessToken = accessToken;
            _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

            // メールアドレスを取得
            string email = await FetchUserEmailAsync(accessToken) ?? "Google ユーザー";

            if (!string.IsNullOrWhiteSpace(customClientId))
            {
                settings.GoogleClientId = customClientId.Trim();
            }
            if (!string.IsNullOrWhiteSpace(customClientSecret))
            {
                settings.GoogleClientSecret = customClientSecret.Trim();
            }

            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                settings.GoogleRefreshTokenEncrypted = SecurityHelper.EncryptString(refreshToken);
            }
            settings.GoogleAccountEmail = email;
            settings.GoogleSyncEnabled = true;
            _saveSettings(settings);

            return (true, $"{email} として認証に成功しました。");
        }
        catch (Exception ex)
        {
            return (false, $"トークン処理失敗: {ex.Message}");
        }
    }

    public void SignOut()
    {
        var settings = _getSettings();
        settings.GoogleRefreshTokenEncrypted = string.Empty;
        settings.GoogleAccountEmail = string.Empty;
        settings.GoogleSyncEnabled = false;
        _saveSettings(settings);

        _cachedAccessToken = null;
        _accessTokenExpiry = DateTime.MinValue;
    }

    /// <summary>
    /// 有効な Google API アクセストークンを取得します (必要に応じて自動リフレッシュ)。
    /// </summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && DateTime.UtcNow < _accessTokenExpiry)
        {
            return _cachedAccessToken;
        }

        var settings = _getSettings();
        if (string.IsNullOrWhiteSpace(settings.GoogleRefreshTokenEncrypted))
        {
            return null;
        }

        string refreshToken = SecurityHelper.DecryptString(settings.GoogleRefreshTokenEncrypted);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        string clientId = !string.IsNullOrWhiteSpace(settings.GoogleClientId) ? settings.GoogleClientId : DefaultClientId;
        var refreshParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        string clientSecret = !string.IsNullOrWhiteSpace(settings.GoogleClientSecret) ? settings.GoogleClientSecret : DefaultClientSecret;
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            refreshParams["client_secret"] = clientSecret;
        }

        try
        {
            var res = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(refreshParams));
            string json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[GoogleOAuth] Token refresh failed HTTP {(int)res.StatusCode}: {json}");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string newAccessToken = root.GetProperty("access_token").GetString() ?? "";
            int expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;

            _cachedAccessToken = newAccessToken;
            _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            return _cachedAccessToken;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GoogleOAuth] Token refresh exception: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> FetchUserEmailAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
