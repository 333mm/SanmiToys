using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SanmiToys.Core.Services;
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
    private System.Threading.Timer? _periodicTimer;
    private string _lastNotifiedUpdateVersion = string.Empty;

    public event Action<UpdateCheckResult>? UpdateFound;

    public void StartPeriodicUpdateCheck(Action<UpdateCheckResult>? onUpdateFound = null, TimeSpan? interval = null)
    {
        if (onUpdateFound != null)
        {
            UpdateFound += onUpdateFound;
        }

        var checkInterval = interval ?? TimeSpan.FromHours(1);
        _periodicTimer?.Dispose();
        _periodicTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                var result = await CheckForUpdatesAsync();
                if (result.HasUpdate && result.LatestVersion != _lastNotifiedUpdateVersion)
                {
                    _lastNotifiedUpdateVersion = result.LatestVersion;
                    UpdateFound?.Invoke(result);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("UpdateService", $"Periodic update check exception: {ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(5), checkInterval);
    }

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SanmiToys-App");

        try
        {
            // prerelease: true にすることでベータ版 (Pre-release) の自動更新も正常に検知・取得
            var source = new GithubSource($"https://github.com/{DefaultGitHubRepo}", null, true);
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

        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly == null || !(entryAssembly.GetName().Name?.StartsWith("SanmiToys", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            entryAssembly = typeof(UpdateService).Assembly;
        }

        var infoVerAttr = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (!string.IsNullOrEmpty(infoVerAttr?.InformationalVersion))
        {
            var raw = infoVerAttr.InformationalVersion.Split('+')[0].Trim();
            if (!string.IsNullOrEmpty(raw))
            {
                return raw;
            }
        }

        var asmVersion = entryAssembly.GetName().Version;
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
                AppLogger.Info("UpdateService", $"Velopack update available: {newVerStr}");
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

            AppLogger.Info("UpdateService", $"Velopack reports no updates available. Current={currentVersion}");
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
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"Velopack check failed: {ex.Message}. Falling back to GitHub API.");
            return await CheckGitHubReleasesAsync(currentVersion);
        }
    }

    public async Task<bool> DownloadAndApplyVelopackUpdateAsync(Action<int>? progressCallback = null)
    {
        if (_updateManager == null)
        {
            AppLogger.Warn("UpdateService", "DownloadAndApplyVelopackUpdateAsync: UpdateManager is null.");
            return false;
        }

        try
        {
            if (_latestUpdateInfo == null)
            {
                AppLogger.Info("UpdateService", "Checking for Velopack updates before download...");
                _latestUpdateInfo = await _updateManager.CheckForUpdatesAsync();
            }

            if (_latestUpdateInfo == null)
            {
                AppLogger.Warn("UpdateService", "No Velopack update available to download.");
                return false;
            }

            AppLogger.Info("UpdateService", $"Starting Velopack download for {_latestUpdateInfo.TargetFullRelease.Version.ToFullString()}...");
            await _updateManager.DownloadUpdatesAsync(_latestUpdateInfo, p => progressCallback?.Invoke(p));
            AppLogger.Info("UpdateService", "Applying Velopack update and restarting...");
            _updateManager.ApplyUpdatesAndRestart(_latestUpdateInfo);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateService", $"Velopack download/apply failed: {ex.Message}");
            return false;
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
                    ReleaseUrl: "ms-windows-store://pdp/?productid=9NQDSVBDSS3M",
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

    public static bool IsNewerVersion(string latestVerStr, string currentVerStr)
    {
        if (string.IsNullOrWhiteSpace(latestVerStr) || string.IsNullOrWhiteSpace(currentVerStr))
            return false;

        string latClean = CleanVersionString(latestVerStr);
        string curClean = CleanVersionString(currentVerStr);

        if (string.Equals(latClean, curClean, StringComparison.OrdinalIgnoreCase))
            return false;

        if (SemanticVersion.TryParse(latClean, out var latSem) && SemanticVersion.TryParse(curClean, out var curSem))
        {
            return latSem > curSem;
        }

        var latBase = latClean.Split('-')[0];
        var curBase = curClean.Split('-')[0];
        if (Version.TryParse(latBase, out var lVer) && Version.TryParse(curBase, out var cVer))
        {
            if (lVer > cVer) return true;
            if (lVer < cVer) return false;
        }

        var latMatch = Regex.Match(latClean, @"\d+$");
        var curMatch = Regex.Match(curClean, @"\d+$");
        if (latMatch.Success && curMatch.Success &&
            int.TryParse(latMatch.Value, out int latNum) &&
            int.TryParse(curMatch.Value, out int curNum))
        {
            return latNum > curNum;
        }

        return string.Compare(latClean, curClean, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string CleanVersionString(string ver)
    {
        ver = ver.Trim().TrimStart('v', 'V');
        return Regex.Replace(ver, @"-([a-zA-Z]+)-(\d+)", "-$1.$2");
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
                AppLogger.Warn("UpdateService", $"GitHub API release check failed: HTTP {response.StatusCode}");
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
            var releaseUrl = node["html_url"]?.GetValue<string>() ?? $"https://github.com/{DefaultGitHubRepo}/releases";
            var body = node["body"]?.GetValue<string>() ?? "";

            // Check if assets contain Velopack packages to extract exact version (e.g. SanmiToys-1.0.0-beta.108-full.nupkg)
            string latestVerClean = "";
            var assets = node["assets"]?.AsArray();
            if (assets != null)
            {
                foreach (var asset in assets)
                {
                    var name = asset?["name"]?.GetValue<string>() ?? "";
                    var match = Regex.Match(name, @"^SanmiToys-(.+?)-full\.nupkg$", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        latestVerClean = match.Groups[1].Value;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(latestVerClean))
            {
                latestVerClean = tagName;
            }

            bool hasUpdate = IsNewerVersion(latestVerClean, currentVersion);
            AppLogger.Info("UpdateService", $"GitHub Check: Current={currentVersion}, Latest={latestVerClean}, HasUpdate={hasUpdate}");

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
            AppLogger.Warn("UpdateService", $"CheckGitHubReleasesAsync exception: {ex.Message}");
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
