using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SanmiToys.Host.Services;

public record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string ReleaseNotes,
    bool IsStoreApp,
    bool IsVelopack = false,
    string? ErrorMessage = null
);

public class UpdateService
{
    private static readonly Lazy<UpdateService> _instance = new(() => new UpdateService());
    public static UpdateService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    public const string DefaultGitHubRepo = "333mm/SanmiToys";

    private UpdateManager? _updateManager;
    private UpdateInfo? _latestUpdateInfo;

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SanmiToys-App");

        try
        {
            var source = new GithubSource($"https://github.com/{DefaultGitHubRepo}", null, false);
            _updateManager = new UpdateManager(source);
        }
        catch
        {
            _updateManager = null;
        }
    }

    public bool IsVelopackInstalled => _updateManager?.IsInstalled ?? false;

    public static bool IsRunningAsPackagedStoreApp()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current != null;
        }
        catch
        {
            return false;
        }
    }

    public string GetCurrentVersionString()
    {
        try
        {
            if (IsRunningAsPackagedStoreApp())
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }
        catch { }

        if (_updateManager != null && _updateManager.IsInstalled && _updateManager.CurrentVersion != null)
        {
            return _updateManager.CurrentVersion.ToFullString();
        }

        var asmVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        return asmVersion != null ? $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}" : "1.0.0";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        string currentVersion = GetCurrentVersionString();

        if (IsRunningAsPackagedStoreApp())
        {
            return await CheckStoreUpdatesAsync(currentVersion);
        }

        if (_updateManager != null && _updateManager.IsInstalled)
        {
            return await CheckVelopackUpdatesAsync(currentVersion);
        }

        return await CheckGitHubReleasesAsync(currentVersion);
    }

    private async Task<UpdateCheckResult> CheckVelopackUpdatesAsync(string currentVersion)
    {
        try
        {
            if (_updateManager == null) return new UpdateCheckResult(false, currentVersion, currentVersion, "", "", false);

            _latestUpdateInfo = await _updateManager.CheckForUpdatesAsync();
            if (_latestUpdateInfo != null)
            {
                var newVerStr = _latestUpdateInfo.TargetFullRelease.Version.ToFullString();
                return new UpdateCheckResult(
                    HasUpdate: true,
                    CurrentVersion: currentVersion,
                    LatestVersion: newVerStr,
                    ReleaseUrl: $"https://github.com/{DefaultGitHubRepo}/releases",
                    ReleaseNotes: $"New version {newVerStr} is ready to download via Velopack.",
                    IsStoreApp: false,
                    IsVelopack: true
                );
            }

            return new UpdateCheckResult(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                ReleaseUrl: "",
                ReleaseNotes: "",
                IsStoreApp: false,
                IsVelopack: true
            );
        }
        catch
        {
            // フォールバックして GitHub API をチェック
            return await CheckGitHubReleasesAsync(currentVersion);
        }
    }

    public async Task DownloadAndApplyVelopackUpdateAsync(Action<int>? progressCallback = null)
    {
        if (_updateManager == null || _latestUpdateInfo == null) return;

        await _updateManager.DownloadUpdatesAsync(_latestUpdateInfo, p => progressCallback?.Invoke(p));
        _updateManager.ApplyUpdatesAndRestart(_latestUpdateInfo);
    }

    private async Task<UpdateCheckResult> CheckStoreUpdatesAsync(string currentVersion)
    {
        try
        {
            var storeContext = Windows.Services.Store.StoreContext.GetDefault();
            var updates = await storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates != null && updates.Count > 0)
            {
                return new UpdateCheckResult(
                    HasUpdate: true,
                    CurrentVersion: currentVersion,
                    LatestVersion: "Store Update Available",
                    ReleaseUrl: "ms-windows-store://pdp/?productid=SanmiToys",
                    ReleaseNotes: "Microsoft Store update is ready to install.",
                    IsStoreApp: true
                );
            }
            return new UpdateCheckResult(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                ReleaseUrl: "",
                ReleaseNotes: "",
                IsStoreApp: true
            );
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                ReleaseUrl: "",
                ReleaseNotes: "",
                IsStoreApp: true,
                ErrorMessage: ex.Message
            );
        }
    }

    private async Task<UpdateCheckResult> CheckGitHubReleasesAsync(string currentVersion)
    {
        try
        {
            // /releases を取得して最新のリリース（プレリリース/ベータ含む）をチェック
            var url = $"https://api.github.com/repos/{DefaultGitHubRepo}/releases?per_page=1";
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    HasUpdate: false,
                    CurrentVersion: currentVersion,
                    LatestVersion: currentVersion,
                    ReleaseUrl: $"https://github.com/{DefaultGitHubRepo}/releases",
                    ReleaseNotes: "",
                    IsStoreApp: false,
                    ErrorMessage: $"HTTP {response.StatusCode}"
                );
            }

            var array = await response.Content.ReadFromJsonAsync<JsonArray>();
            if (array == null || array.Count == 0)
            {
                return new UpdateCheckResult(false, currentVersion, currentVersion, "", "", false);
            }

            var node = array[0]?.AsObject();
            if (node == null)
            {
                return new UpdateCheckResult(false, currentVersion, currentVersion, "", "", false);
            }

            var tagName = node["tag_name"]?.GetValue<string>() ?? "";
            var latestVerClean = tagName.TrimStart('v', 'V');
            var releaseUrl = node["html_url"]?.GetValue<string>() ?? $"https://github.com/{DefaultGitHubRepo}/releases";
            var body = node["body"]?.GetValue<string>() ?? "";

            bool hasUpdate = false;
            var curVerStr = currentVersion.Split('-')[0];
            var latVerStr = latestVerClean.Split('-')[0];

            if (Version.TryParse(latVerStr, out var latestVer) && Version.TryParse(curVerStr, out var curVer))
            {
                if (latestVer > curVer)
                {
                    hasUpdate = true;
                }
                else if (latestVer == curVer && latestVerClean != currentVersion)
                {
                    hasUpdate = string.Compare(latestVerClean, currentVersion, StringComparison.OrdinalIgnoreCase) > 0;
                }
            }

            return new UpdateCheckResult(
                HasUpdate: hasUpdate,
                CurrentVersion: currentVersion,
                LatestVersion: latestVerClean,
                ReleaseUrl: releaseUrl,
                ReleaseNotes: body,
                IsStoreApp: false,
                IsVelopack: false
            );
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                ReleaseUrl: "",
                ReleaseNotes: "",
                IsStoreApp: false,
                ErrorMessage: ex.Message
            );
        }
    }

    public async Task<bool> TriggerStoreUpdateInstallationAsync()
    {
        if (!IsRunningAsPackagedStoreApp()) return false;

        try
        {
            var storeContext = Windows.Services.Store.StoreContext.GetDefault();
            var updates = await storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates != null && updates.Count > 0)
            {
                var result = await storeContext.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                return result.OverallState == Windows.Services.Store.StorePackageUpdateState.Completed;
            }
        }
        catch { }

        return false;
    }
}
