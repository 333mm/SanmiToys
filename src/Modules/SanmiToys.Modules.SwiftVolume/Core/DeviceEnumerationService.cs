using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SanmiToys.Modules.SwiftVolume.Core;

public class SafeDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public float Volume { get; set; }
    public bool IsMuted { get; set; }
}

public class SafeAudioSession
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public uint ProcessId { get; set; }
    public float Volume { get; set; }
    public bool IsMuted { get; set; }
    public BitmapSource? Icon { get; set; }
    public AudioSessionControl? Control { get; set; }
    public List<AudioSessionControl> Controls { get; set; } = new();
    public List<SafeAudioSession> ChildSessions { get; set; } = new();
}

public class DeviceEnumerationService : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, BitmapSource? Icon)> _processMetaCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string Name, BitmapSource? Icon)> _pidMetaCache = new();

    public static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fxsound",
        "fxsound_dsp",
        "audiodg",
        "dwm",
        "steelseriessonar",
        "sonar",
        "voicemeeter",
        "voicemeeter8",
        "voicemeeter8x64",
        "nvaudioeffects",
        "nvbroadcast",
        "sanmitoys",
        "sanmitoys.host"
    };

    public static bool IsExcludedSession(string processName, string? displayName, uint pid)
    {
        if (pid > 0 && pid == (uint)Environment.ProcessId) return true;
        if (!string.IsNullOrEmpty(processName))
        {
            if (ExcludedProcessNames.Contains(processName)) return true;
            if (processName.Contains("FxSound", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (!string.IsNullOrEmpty(displayName))
        {
            if (displayName.Contains("FxSound", StringComparison.OrdinalIgnoreCase)) return true;
            if (ExcludedProcessNames.Contains(displayName)) return true;
        }
        return false;
    }

    public DeviceEnumerationService()
    {
    }

    /// <summary>
    /// 指定されたセッションが現在も生存しており、有効なオーディオコントロールを保持しているか判定
    /// </summary>
    public static bool IsSessionAlive(SafeAudioSession? session)
    {
        if (session == null) return false;

        // システムサウンドはプロセスIDが0の場合があるが常に有効
        if (session.ProcessId == 0) return true;

        // プロセス自体の生存確認
        if (!SwiftVolumeNativeMethods.IsProcessAlive(session.ProcessId))
            return false;

        // 保持されているすべての AudioSessionControl を収集
        var allControls = new List<AudioSessionControl>();
        if (session.Controls != null && session.Controls.Count > 0)
        {
            allControls.AddRange(session.Controls);
        }
        if (session.Control != null && !allControls.Contains(session.Control))
        {
            allControls.Add(session.Control);
        }
        if (session.ChildSessions != null && session.ChildSessions.Count > 0)
        {
            foreach (var child in session.ChildSessions)
            {
                if (child.Controls != null && child.Controls.Count > 0)
                {
                    allControls.AddRange(child.Controls);
                }
                if (child.Control != null && !allControls.Contains(child.Control))
                {
                    allControls.Add(child.Control);
                }
            }
        }

        if (allControls.Count == 0)
        {
            return true;
        }

        bool hasValidControl = false;
        foreach (var c in allControls)
        {
            if (c == null) continue;
            try
            {
                if (c.State != AudioSessionState.AudioSessionStateExpired)
                {
                    hasValidControl = true;
                    break;
                }
            }
            catch
            {
                // COMException (RPC_E_DISCONNECTED 等) はセッション消滅
            }
        }

        return hasValidControl;
    }

    public static (string Name, BitmapSource? Icon) GetProcessMeta(uint pid)
    {
        bool isJa = SanmiToys.Core.Services.LocalizationService.Instance.EffectiveLanguageCode == "ja";
        string sysSoundName = isJa ? "システム サウンド" : "System Sounds";

        if (pid == 0)
        {
            if (_pidMetaCache.TryGetValue(0, out var sysMeta)) return sysMeta;
            var res = (sysSoundName, (BitmapSource?)null);
            _pidMetaCache[0] = res;
            return res;
        }

        if (_pidMetaCache.TryGetValue(pid, out var pidMeta))
        {
            return pidMeta;
        }

        string? exePath = GetProcessExePath(pid);
        if (!string.IsNullOrEmpty(exePath) && _processMetaCache.TryGetValue(exePath, out var cachedMeta))
        {
            _pidMetaCache[pid] = cachedMeta;
            return cachedMeta;
        }

        string name = string.Empty;
        BitmapSource? icon = null;

        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                {
                    name = vi.FileDescription;
                }
                else if (!string.IsNullOrWhiteSpace(vi.ProductName))
                {
                    name = vi.ProductName;
                }
            }
            catch { }

            try
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    icon.Freeze();
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                name = Path.GetFileNameWithoutExtension(exePath);
            }
            else
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    name = proc.ProcessName;
                }
                catch
                {
                    name = $"Process {pid}";
                }
            }
        }

        if (icon == null && name.Equals("svchost", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string sysSvchost = Path.Combine(Environment.SystemDirectory, "svchost.exe");
                if (File.Exists(sysSvchost))
                {
                    using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(sysSvchost);
                    if (sysIcon != null)
                    {
                        icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            sysIcon.Handle,
                            System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        icon.Freeze();
                    }
                }
            }
            catch { }
        }

        var result = (name, icon);
        if (!string.IsNullOrEmpty(exePath))
        {
            _processMetaCache[exePath] = result;
        }
        _pidMetaCache[pid] = result;
        return result;
    }

    public static (string Name, BitmapSource? Icon) GetProcessMetaFromExeOrName(string exeOrPath)
    {
        if (string.IsNullOrEmpty(exeOrPath)) return ("Application", null);
        string fileName = Path.GetFileNameWithoutExtension(exeOrPath);

        // キャッシュ確認
        if (_processMetaCache.TryGetValue(exeOrPath, out var cached)) return cached;

        if (File.Exists(exeOrPath))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exeOrPath);
                string name = !string.IsNullOrWhiteSpace(vi.FileDescription)
                    ? vi.FileDescription
                    : (!string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName : fileName);

                BitmapSource? icon = null;
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exeOrPath);
                if (sysIcon != null)
                {
                    icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    icon.Freeze();
                }
                var res = (name, icon);
                _processMetaCache[exeOrPath] = res;
                return res;
            }
            catch { }
        }

        return (fileName, null);
    }

    public static uint ExtractPidFromSessionInstanceId(string? instId)
    {
        if (string.IsNullOrEmpty(instId)) return 0;
        try
        {
            int lastPercentB = instId.LastIndexOf("%b", StringComparison.OrdinalIgnoreCase);
            if (lastPercentB >= 0 && lastPercentB + 2 < instId.Length)
            {
                string pidStr = instId.Substring(lastPercentB + 2).Trim();
                int sepIdx = pidStr.IndexOfAny(new[] { '|', '}', ' ', '"', '\r', '\n' });
                if (sepIdx > 0) pidStr = pidStr.Substring(0, sepIdx);
                if (uint.TryParse(pidStr, out uint pid) && pid > 0)
                {
                    return pid;
                }
            }
        }
        catch { }
        return 0;
    }

    public static string? ExtractExePathFromSessionInstanceId(string? instId)
    {
        if (string.IsNullOrEmpty(instId)) return null;
        try
        {
            int pipeIdx = instId.IndexOf('|');
            if (pipeIdx >= 0)
            {
                string pathPart = instId.Substring(pipeIdx + 1);
                int percentB = pathPart.IndexOf("%b", StringComparison.OrdinalIgnoreCase);
                if (percentB >= 0)
                {
                    pathPart = pathPart.Substring(0, percentB);
                }
                pathPart = pathPart.Trim();
                if (pathPart.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return pathPart;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? GetProcessExePath(uint pid)
    {
        if (pid == 0) return null;
        IntPtr hProcess = SwiftVolumeNativeMethods.OpenProcess(SwiftVolumeNativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (SwiftVolumeNativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
            {
                return buffer.ToString();
            }
        }
        finally
        {
            SwiftVolumeNativeMethods.CloseHandle(hProcess);
        }
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _pendingDeviceVolumes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _deviceVolumeWorkers = new();
    private static readonly object _volWorkerLock = new();

    public void SetDeviceVolume(string deviceId, float volumePercent)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDevice(deviceId);
            if (dev != null)
            {
                float next = Math.Clamp(volumePercent, 0f, 100f);
                dev.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;
                if (next > 0 && dev.AudioEndpointVolume.Mute)
                {
                    dev.AudioEndpointVolume.Mute = false;
                }
            }
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"SetDeviceVolume warning for {deviceId}: {ex.Message}");
        }
    }

    public System.Threading.Tasks.Task SetDeviceVolumeAsync(string deviceId, float volumePercent)
    {
        if (string.IsNullOrEmpty(deviceId)) return System.Threading.Tasks.Task.CompletedTask;

        _pendingDeviceVolumes[deviceId] = volumePercent;

        lock (_volWorkerLock)
        {
            if (_deviceVolumeWorkers.TryGetValue(deviceId, out bool running) && running)
            {
                // 既にワーカーが処理中のため、最新値 (_pendingDeviceVolumes) が次回ループで自動反映される
                return System.Threading.Tasks.Task.CompletedTask;
            }
            _deviceVolumeWorkers[deviceId] = true;
        }

        return System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var dev = enumerator.GetDevice(deviceId);
                if (dev != null)
                {
                    while (_pendingDeviceVolumes.TryRemove(deviceId, out float targetVol))
                    {
                        float next = Math.Clamp(targetVol, 0f, 100f);
                        float targetScalar = next / 100f;
                        float curScalar = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                        if (Math.Abs(curScalar - targetScalar) > 0.002f)
                        {
                            dev.AudioEndpointVolume.MasterVolumeLevelScalar = targetScalar;
                        }
                        if (next > 0 && dev.AudioEndpointVolume.Mute)
                        {
                            dev.AudioEndpointVolume.Mute = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"SetDeviceVolume warning for {deviceId}: {ex.Message}");
            }
            finally
            {
                lock (_volWorkerLock)
                {
                    _deviceVolumeWorkers[deviceId] = false;
                }
            }
        });
    }

    public void SetDeviceVolumeDirect(string deviceId, float volumePercent)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDevice(deviceId);
            if (dev != null)
            {
                float next = Math.Clamp(volumePercent, 0f, 100f);
                float targetScalar = next / 100f;
                float curScalar = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                if (Math.Abs(curScalar - targetScalar) > 0.005f)
                {
                    dev.AudioEndpointVolume.MasterVolumeLevelScalar = targetScalar;
                }
                if (next > 0 && dev.AudioEndpointVolume.Mute)
                {
                    dev.AudioEndpointVolume.Mute = false;
                }
            }
        }
        catch { }
    }

    public List<SafeDeviceInfo> GetSafeOutputDevices()
    {
        for (int retry = 0; retry < 3; retry++)
        {
            var result = new List<SafeDeviceInfo>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                string defaultId = "";
                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def?.ID ?? "";
                }
                catch { }

                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var d in devices)
                {
                    try
                    {
                        string id = "";
                        string name = "";
                        try { id = d.ID; } catch { continue; }
                        try { name = d.FriendlyName; } catch { name = id; }

                        float vol = 0.5f;
                        bool muted = false;
                        try
                        {
                            vol = d.AudioEndpointVolume.MasterVolumeLevelScalar;
                            muted = d.AudioEndpointVolume.Mute;
                        }
                        catch { }

                        result.Add(new SafeDeviceInfo
                        {
                            Id = id,
                            Name = string.IsNullOrWhiteSpace(name) ? "Audio Device" : name,
                            IsDefault = id == defaultId,
                            Volume = vol,
                            IsMuted = muted
                        });
                    }
                    catch { }
                    finally
                    {
                        try { d.Dispose(); } catch { }
                    }
                }

                return result;
            }
            catch { }

            if (retry < 2) System.Threading.Thread.Sleep(30);
        }
        return new List<SafeDeviceInfo>();
    }

    public List<SafeDeviceInfo> GetSafeInputDevices()
    {
        for (int retry = 0; retry < 2; retry++)
        {
            var result = new List<SafeDeviceInfo>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                string defaultId = "";
                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    defaultId = def?.ID ?? "";
                }
                catch { }

                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var d in devices)
                {
                    try
                    {
                        string id = "";
                        string name = "";
                        try { id = d.ID; } catch { continue; }
                        try { name = d.FriendlyName; } catch { name = id; }

                        float vol = 0.5f;
                        bool muted = false;
                        try
                        {
                            vol = d.AudioEndpointVolume.MasterVolumeLevelScalar;
                            muted = d.AudioEndpointVolume.Mute;
                        }
                        catch { }

                        result.Add(new SafeDeviceInfo
                        {
                            Id = id,
                            Name = string.IsNullOrWhiteSpace(name) ? "Input Device" : name,
                            IsDefault = id == defaultId,
                            Volume = vol,
                            IsMuted = muted
                        });
                    }
                    catch { }
                    finally
                    {
                        try { d.Dispose(); } catch { }
                    }
                }

                return result;
            }
            catch { }

            if (retry < 2) System.Threading.Thread.Sleep(30);
        }
        return new List<SafeDeviceInfo>();
    }

    public List<SafeAudioSession> GetSafeSessions(string deviceId)
    {
        var rawSessions = new List<SafeAudioSession>();
        if (string.IsNullOrEmpty(deviceId)) return rawSessions;

        for (int retry = 0; retry < 2; retry++)
        {
            rawSessions.Clear();
            bool querySucceeded = false;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? dev = null;
                try
                {
                    dev = enumerator.GetDevice(deviceId);
                }
                catch
                {
                    // デバイスIDで見つからない場合は既定デバイスにフォールバック
                    try { dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
                }

                if (dev != null)
                {
                    using (dev)
                    {
                        var sessionManager = dev.AudioSessionManager;
                        if (sessionManager != null)
                        {
                            var sessions = sessionManager.Sessions;
                            int count = 0;
                            try { count = sessions.Count; } catch { count = 0; }
                            querySucceeded = true;

                            for (int i = 0; i < count; i++)
                            {
                                AudioSessionControl? s = null;
                                try { s = sessions[i]; } catch { continue; }
                                if (s == null) continue;

                                try
                                {
                                    if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                                    string instId = "";
                                    try { instId = s.GetSessionInstanceIdentifier ?? ""; } catch { }

                                    uint pid = 0;
                                    try { pid = s.GetProcessID; } catch { pid = 0; }
                                    if (pid == 0 && !string.IsNullOrEmpty(instId))
                                    {
                                        pid = ExtractPidFromSessionInstanceId(instId);
                                    }

                                    bool isSysSound = false;
                                    try { isSysSound = s.IsSystemSoundsSession; } catch { }

                                    string iconPath = "";
                                    string dName = "";
                                    try { iconPath = s.IconPath ?? ""; } catch { }
                                    try { dName = s.DisplayName ?? ""; } catch { }

                                    if (!isSysSound)
                                    {
                                        if (iconPath.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                                            iconPath.Contains("shell32", StringComparison.OrdinalIgnoreCase) ||
                                            dName.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                                            dName.Contains("System Sound", StringComparison.OrdinalIgnoreCase) ||
                                            dName.Contains("システム サウンド", StringComparison.OrdinalIgnoreCase) ||
                                            dName.Contains("システム", StringComparison.OrdinalIgnoreCase) ||
                                            instId.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase))
                                        {
                                            isSysSound = true;
                                        }
                                    }

                                    // 生存プロセス確認: pid > 0 の場合のみチェック（未再生・起動直後で pid == 0 でも破棄しない）
                                    if (!isSysSound && pid > 0 && !SwiftVolumeNativeMethods.IsProcessAlive(pid))
                                    {
                                        continue;
                                    }

                                    // 自プロセス（SanmiToys）自身のセッションはミキサーに表示しない
                                    uint myPid = (uint)Environment.ProcessId;
                                    if (!isSysSound && pid == myPid)
                                    {
                                        continue;
                                    }

                                    string name = string.Empty;
                                    BitmapSource? icon = null;

                                    if (isSysSound)
                                    {
                                        var meta = GetProcessMeta(0);
                                        name = meta.Name;
                                        icon = meta.Icon;
                                    }
                                    else if (pid > 0)
                                    {
                                        var meta = GetProcessMeta(pid);
                                        name = meta.Name;
                                        icon = meta.Icon;
                                    }
                                    else
                                    {
                                        // pid == 0 だが isSysSound ではない場合: instId から exe パスを取得、または DisplayName を活用
                                        string? exePath = ExtractExePathFromSessionInstanceId(instId);
                                        if (!string.IsNullOrEmpty(exePath))
                                        {
                                            var meta = GetProcessMetaFromExeOrName(exePath);
                                            name = meta.Name;
                                            icon = meta.Icon;
                                        }
                                        else if (!string.IsNullOrWhiteSpace(dName))
                                        {
                                            name = dName;
                                        }
                                        else
                                        {
                                            name = "Application";
                                        }
                                    }

                                    // Windows音量ミキサーと同様、FxSound等のオーディオ中継・仮想ドライバ・システムインフラプロセスは除外
                                    if (!isSysSound)
                                    {
                                        if (IsExcludedSession(name, dName, pid))
                                        {
                                            // 除外されたアプリ（FxSound等）の音量はデフォルト（100% / ミュート解除）に確実に復帰
                                            // 未登録アプリの音量設定（SVオプション）等には絶対に引きずられないようにする
                                            try
                                            {
                                                if (Math.Abs(s.SimpleAudioVolume.Volume - 1.0f) > 0.005f)
                                                {
                                                    s.SimpleAudioVolume.Volume = 1.0f;
                                                }
                                                if (s.SimpleAudioVolume.Mute)
                                                {
                                                    s.SimpleAudioVolume.Mute = false;
                                                }
                                            }
                                            catch { }
                                            continue;
                                        }
                                    }

                                    float vol = 1.0f;
                                    bool muted = false;
                                    try
                                    {
                                        vol = s.SimpleAudioVolume.Volume;
                                        muted = s.SimpleAudioVolume.Mute;
                                    }
                                    catch { }

                                    string displayName = isSysSound
                                        ? (SanmiToys.Core.Services.LocalizationService.Instance.EffectiveLanguageCode == "ja" ? "システム サウンド" : "System Sounds")
                                        : name;

                                    if (!isSysSound && name.Equals("svchost", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dName))
                                    {
                                        displayName = $"svchost ({dName})";
                                    }

                                    var sessionItem = new SafeAudioSession
                                    {
                                        Id = $"{deviceId}_{(isSysSound ? 0 : pid)}_{i}",
                                        DisplayName = displayName,
                                        ProcessId = isSysSound ? 0 : pid,
                                        Volume = vol,
                                        IsMuted = muted,
                                        Icon = isSysSound ? null : icon,
                                        Control = s
                                    };
                                    sessionItem.Controls.Add(s);
                                    rawSessions.Add(sessionItem);
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            if (querySucceeded || rawSessions.Count > 0) break;
            if (retry < 1) System.Threading.Thread.Sleep(30);
        }

        if (rawSessions.Count == 0) return rawSessions;

        // 同一アプリ名・プロセス名のセッションを1つにグルーピング（折りたたみ）
        var grouped = new List<SafeAudioSession>();
        foreach (var group in rawSessions.GroupBy(x => x.DisplayName))
        {
            var parent = group.First();
            parent.Controls = group.SelectMany(g => g.Controls).Where(c => c != null).Distinct().ToList();
            parent.ChildSessions = group.ToList();
            // いずれかがミュートされていれば代表ミュート、音量は最大値を採用
            parent.Volume = group.Max(g => g.Volume);
            parent.IsMuted = group.All(g => g.IsMuted);
            grouped.Add(parent);
        }
        return grouped;
    }

    /// <summary>
    /// 全オーディオ出力デバイスを巡回し、除外対象アプリ（FxSound、オーディオ中継ソフト、システム等）の
    /// セッション音量を確実に 100% (1.0f) かつミュート解除に復帰させる。
    /// （未登録アプリの音量設定等には絶対に引きずられないようにする）
    /// </summary>
    public void ResetExcludedSessionsVolumeToDefault()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var dev in devices)
            {
                try
                {
                    using (dev)
                    {
                        var sessionManager = dev.AudioSessionManager;
                        if (sessionManager == null) continue;
                        var sessions = sessionManager.Sessions;
                        int count = 0;
                        try { count = sessions.Count; } catch { count = 0; }

                        for (int i = 0; i < count; i++)
                        {
                            AudioSessionControl? s = null;
                            try { s = sessions[i]; } catch { continue; }
                            if (s == null) continue;

                            try
                            {
                                if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                                uint pid = 0;
                                try { pid = s.GetProcessID; } catch { }
                                string dName = s.DisplayName ?? "";
                                var (name, _) = GetProcessMeta(pid);

                                if (IsExcludedSession(name, dName, pid))
                                {
                                    if (Math.Abs(s.SimpleAudioVolume.Volume - 1.0f) > 0.005f)
                                    {
                                        s.SimpleAudioVolume.Volume = 1.0f;
                                    }
                                    if (s.SimpleAudioVolume.Mute)
                                    {
                                        s.SimpleAudioVolume.Mute = false;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        // 列挙器は各操作内で破棄済み。
    }
}
