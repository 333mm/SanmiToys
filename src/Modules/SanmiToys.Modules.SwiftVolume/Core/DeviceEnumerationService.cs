using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            BitmapSource? sysIcon = null;
            try
            {
                string shell32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
                if (File.Exists(shell32))
                {
                    using var shIcon = System.Drawing.Icon.ExtractAssociatedIcon(shell32);
                    if (shIcon != null)
                    {
                        sysIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            shIcon.Handle,
                            System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        sysIcon.Freeze();
                    }
                }
            }
            catch { }
            var res = (sysSoundName, sysIcon);
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
        IntPtr hProcess = SwiftVolumeNativeMethods.OpenProcess(SwiftVolumeNativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != IntPtr.Zero)
        {
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                int size = buffer.Capacity;
                if (SwiftVolumeNativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                {
                    return buffer.ToString();
                }
            }
            catch { }
            finally
            {
                SwiftVolumeNativeMethods.CloseHandle(hProcess);
            }
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public List<SafeDeviceInfo> GetSafeOutputDevices()
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
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def?.ID ?? "";
                }
                catch { }

                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var d in devices)
                {
                    try
                    {
                        result.Add(new SafeDeviceInfo
                        {
                            Id = d.ID,
                            Name = d.FriendlyName,
                            IsDefault = d.ID == defaultId,
                            Volume = d.AudioEndpointVolume.MasterVolumeLevelScalar,
                            IsMuted = d.AudioEndpointVolume.Mute
                        });
                    }
                    catch { }
                    finally
                    {
                        try { d.Dispose(); } catch { }
                    }
                }

                if (result.Count > 0) return result;
            }
            catch { }

            if (retry == 0) System.Threading.Thread.Sleep(50);
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
                        result.Add(new SafeDeviceInfo
                        {
                            Id = d.ID,
                            Name = d.FriendlyName,
                            IsDefault = d.ID == defaultId,
                            Volume = d.AudioEndpointVolume.MasterVolumeLevelScalar,
                            IsMuted = d.AudioEndpointVolume.Mute
                        });
                    }
                    catch { }
                    finally
                    {
                        try { d.Dispose(); } catch { }
                    }
                }

                if (result.Count > 0) return result;
            }
            catch { }

            if (retry == 0) System.Threading.Thread.Sleep(50);
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
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var dev = enumerator.GetDevice(deviceId);
                var sessionManager = dev?.AudioSessionManager;
                if (sessionManager != null)
                {
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
                            uint pid = 0;
                            bool isSysSound = false;
                            try { isSysSound = s.IsSystemSoundsSession; } catch { }

                            if (!isSysSound)
                            {
                                try { pid = s.GetProcessID; } catch { pid = 0; }
                                if (pid == 0) isSysSound = true;
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
                                Id = $"{deviceId}_{pid}_{i}",
                                DisplayName = name,
                                ProcessId = isSysSound ? 0 : pid,
                                Volume = vol,
                                IsMuted = muted,
                                Icon = icon,
                                Control = s
                            };
                            sessionItem.Controls.Add(s);
                            rawSessions.Add(sessionItem);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (rawSessions.Count > 0) break;
            if (retry == 0) System.Threading.Thread.Sleep(50);
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
