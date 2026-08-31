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

    public DeviceEnumerationService()
    {
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
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                name = !string.IsNullOrWhiteSpace(proc.MainWindowTitle) ? proc.MainWindowTitle : proc.ProcessName;
            }
            catch
            {
                if (!string.IsNullOrEmpty(exePath))
                {
                    name = Path.GetFileNameWithoutExtension(exePath);
                }
                else
                {
                    name = $"Process {pid}";
                }
            }
        }

        var result = (name, icon);
        if (!string.IsNullOrEmpty(exePath))
        {
            _processMetaCache[exePath] = result;
        }
        _pidMetaCache[pid] = result;
        return result;
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
                                    uint pid = 0;
                                    bool isSysSound = false;
                                    try { isSysSound = s.IsSystemSoundsSession; } catch { }

                                    if (!isSysSound)
                                    {
                                        try { pid = s.GetProcessID; } catch { pid = 0; }
                                        if (pid == 0) isSysSound = true;
                                    }

                                    if (!isSysSound)
                                    {
                                        try
                                        {
                                            string iconPath = s.IconPath ?? "";
                                            string dName = s.DisplayName ?? "";
                                            if (iconPath.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                                                iconPath.Contains("shell32", StringComparison.OrdinalIgnoreCase) ||
                                                dName.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                                                dName.Contains("System Sound", StringComparison.OrdinalIgnoreCase) ||
                                                dName.Contains("システム サウンド", StringComparison.OrdinalIgnoreCase) ||
                                                dName.Contains("システム", StringComparison.OrdinalIgnoreCase))
                                            {
                                                isSysSound = true;
                                            }
                                        }
                                        catch { }
                                    }

                                    var (name, icon) = GetProcessMeta(isSysSound ? 0 : pid);

                                    float vol = 1.0f;
                                    bool muted = false;
                                    try
                                    {
                                        vol = s.SimpleAudioVolume.Volume;
                                        muted = s.SimpleAudioVolume.Mute;
                                    }
                                    catch { }

                                    var sessionItem = new SafeAudioSession
                                    {
                                        Id = $"{deviceId}_{(isSysSound ? 0 : pid)}_{i}",
                                        DisplayName = isSysSound ? (SanmiToys.Core.Services.LocalizationService.Instance.EffectiveLanguageCode == "ja" ? "システム サウンド" : "System Sounds") : name,
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

    public void Dispose()
    {
        // 列挙器は各操作内で破棄済み。
    }
}
