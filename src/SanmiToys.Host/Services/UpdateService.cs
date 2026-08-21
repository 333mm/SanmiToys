using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SanmiToys.Host.Services;

public record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string ReleaseNotes,
    bool IsStoreApp,
    string? ErrorMessage = null
);

public class UpdateService
{
    private static readonly Lazy<UpdateService> _instance = new(() => new UpdateService());
    public static UpdateService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    public const string DefaultGitHubRepo = "333mm/SanmiToys";

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SanmiToys-App");
    }

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
        else
        {
            return await CheckGitHubReleasesAsync(currentVersion);
        }
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
            var url = $"https://api.github.com/repos/{DefaultGitHubRepo}/releases/latest";
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

            var node = await response.Content.ReadFromJsonAsync<JsonObject>();
            if (node == null)
            {
                return new UpdateCheckResult(false, currentVersion, currentVersion, "", "", false);
            }

            var tagName = node["tag_name"]?.GetValue<string>() ?? "";
            var latestVerClean = tagName.TrimStart('v', 'V');
            var releaseUrl = node["html_url"]?.GetValue<string>() ?? $"https://github.com/{DefaultGitHubRepo}/releases";
            var body = node["body"]?.GetValue<string>() ?? "";

            bool hasUpdate = false;
            if (Version.TryParse(latestVerClean, out var latestVer) && Version.TryParse(currentVersion, out var curVer))
            {
                hasUpdate = latestVer > curVer;
            }

            return new UpdateCheckResult(
                HasUpdate: hasUpdate,
                CurrentVersion: currentVersion,
                LatestVersion: latestVerClean,
                ReleaseUrl: releaseUrl,
                ReleaseNotes: body,
                IsStoreApp: false
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
