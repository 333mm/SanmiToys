using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SanmiToys.Modules.SwiftVolume.Core;

public class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float Volume { get; set; }
    public bool IsMuted { get; set; }
    public bool IsDefault { get; set; }
}

public static class AudioDeviceHelper
{
    private static MMDeviceEnumerator? _notificationEnumerator;
    private static MMDevice? _currentDefaultDevice;
    private static AudioEndpointVolumeNotificationDelegate? _volumeNotificationHandler;
    private static readonly AudioNotificationClient _notificationClient = new();
    private static readonly object _deviceLock = new();
    private static int _syncRequestId = 0;
    private static string _lastDefaultDeviceId = "";
    private static volatile string _cachedDefaultDeviceName = "";
    private static float _lastSyncedVolume = -1f;
    private static bool _lastSyncedMuted = false;

    public static event Action<float, bool>? MasterVolumeChanged;
    public static event Action? DefaultDeviceChanged;

    static AudioDeviceHelper()
    {
        try
        {
            _notificationEnumerator = new MMDeviceEnumerator();
            _notificationEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Error("SwiftVolume", "Failed to register endpoint notification callback", ex);
        }

        try
        {
            AttachToDefaultDevice();
        }
        catch { }
    }

    private class AudioNotificationClient : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
        {
            if (dataFlow == DataFlow.Render && role == Role.Multimedia)
            {
                ScheduleDeviceChange();
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            ScheduleDeviceChange();
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            ScheduleDeviceChange();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            ScheduleDeviceChange();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        private static void ScheduleDeviceChange()
        {
            int currentId = Interlocked.Increment(ref _syncRequestId);

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // 連続変更のバウンスを最速で吸収 (30ms)
                    // CancellationToken を使用せず世代IDで比較することで TaskCanceledException のスローを防止
                    await System.Threading.Tasks.Task.Delay(30);
                    if (Interlocked.CompareExchange(ref _syncRequestId, 0, 0) != currentId)
                    {
                        return;
                    }

                    PerformDeviceSync();
                }
                catch (Exception ex)
                {
                    SanmiToys.Core.Services.AppLogger.Error("SwiftVolume", "Error in ScheduleDeviceChange", ex);
                }
            });
        }

        private static void PerformDeviceSync()
        {
            try
            {
                bool deviceChanged = AttachToDefaultDevice();
                if (deviceChanged)
                {
                    DefaultDeviceChanged?.Invoke();
                }

                float vol = GetMasterVolume();
                bool muted = GetIsMuted();

                bool volumeChanged = Math.Abs(_lastSyncedVolume - vol) > 0.5f || _lastSyncedMuted != muted;
                if (deviceChanged || volumeChanged)
                {
                    _lastSyncedVolume = vol;
                    _lastSyncedMuted = muted;
                    MasterVolumeChanged?.Invoke(vol, muted);
                    SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"Default audio device synced: {GetDefaultDeviceName()} (Vol: {vol:F0}%, Muted: {muted})");
                }
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Error during audio device sync: {ex.Message}");
            }
        }
    }

    private static bool AttachToDefaultDevice()
    {
        // 1. 重い COM 操作（エンドポイント取得・プロパティ読み取り）を lock の外で先行実行し、UIスレッドの巻き込みフリーズを根絶
        MMDevice? newDevice = null;
        string newId = "";
        string newName = "";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            newDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            newId = newDevice?.ID ?? "";
            newName = newDevice?.FriendlyName ?? "";
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"GetDefaultAudioEndpoint failed: {ex.Message}");
            newDevice = null;
        }

        // 2. 短時間の参照差し替えのみを lock 内で実行
        lock (_deviceLock)
        {
            try
            {
                // 既存のデバイスが有効で同じIDの場合は再アタッチをスキップ
                if (_currentDefaultDevice != null && !string.IsNullOrEmpty(newId) && _currentDefaultDevice.ID == newId)
                {
                    try
                    {
                        _ = _currentDefaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                        newDevice?.Dispose();
                        if (!string.IsNullOrEmpty(newName)) _cachedDefaultDeviceName = newName;
                        return false;
                    }
                    catch
                    {
                        // 既存COMオブジェクトが無効化されていた場合は再アタッチへ
                    }
                }

                if (_currentDefaultDevice != null)
                {
                    if (_volumeNotificationHandler != null)
                    {
                        try { _currentDefaultDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeNotificationHandler; } catch { }
                    }
                    try { _currentDefaultDevice.Dispose(); } catch { }
                    _currentDefaultDevice = null;
                }

                bool deviceChanged = _lastDefaultDeviceId != newId;
                _lastDefaultDeviceId = newId;
                _currentDefaultDevice = newDevice;
                if (!string.IsNullOrEmpty(newName)) _cachedDefaultDeviceName = newName;

                if (_currentDefaultDevice != null)
                {
                    // デバイス切り替え時、一瞬 100% の爆音が出るのを防ぐため、音声を流す前に即座に保存音量を適用
                    if (deviceChanged)
                    {
                        try
                        {
                            var svSettings = SanmiToys.Core.Services.SettingsService.Instance.GetModuleSettings<SanmiToys.Modules.SwiftVolume.Models.SwiftVolumeSettings>("SwiftVolume");
                            if (svSettings != null)
                            {
                                string devName = newName;
                                string effKey = GetEffectiveDeviceVolumeKey(devName);
                                if (svSettings.DeviceMasterVolumes.TryGetValue(effKey, out float savedVol) ||
                                    (svSettings.DeviceMasterVolumes.TryGetValue(devName, out savedVol) && savedVol < 0.99f))
                                {
                                    _currentDefaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = savedVol;
                                }
                                else if (devName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
                                {
                                    var (fxId, fxName) = GetFxSoundOutputDevice();
                                    var fxVol = GetDeviceVolumeDirect(fxId, fxName);
                                    if (fxVol.HasValue && fxVol.Value >= 0f)
                                    {
                                        _currentDefaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = fxVol.Value / 100f;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    _volumeNotificationHandler = (data) =>
                    {
                        MasterVolumeChanged?.Invoke(data.MasterVolume * 100f, data.Muted);
                    };
                    _currentDefaultDevice.AudioEndpointVolume.OnVolumeNotification += _volumeNotificationHandler;
                }

                return deviceChanged;
            }
            catch (Exception ex)
            {
                _currentDefaultDevice = null;
                _lastDefaultDeviceId = "";
                _cachedDefaultDeviceName = "";
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"AttachToDefaultDevice warning: {ex.Message}");
                return false;
            }
        }
    }

    public static void Reinitialize()
    {
        lock (_deviceLock)
        {
            if (_currentDefaultDevice != null)
            {
                if (_volumeNotificationHandler != null)
                {
                    try { _currentDefaultDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeNotificationHandler; } catch { }
                }
                try { _currentDefaultDevice.Dispose(); } catch { }
                _currentDefaultDevice = null;
            }
            _lastDefaultDeviceId = "";
            _cachedDefaultDeviceName = "";

            if (_notificationEnumerator != null)
            {
                try { _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
                try { _notificationEnumerator.Dispose(); } catch { }
                _notificationEnumerator = null;
            }

            try
            {
                _notificationEnumerator = new MMDeviceEnumerator();
                _notificationEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Reinitialize enumerator failed: {ex.Message}");
            }
        }

        AttachToDefaultDevice();
        DefaultDeviceChanged?.Invoke();
    }

    public static void RefreshNotificationBinding()
    {
        Reinitialize();
    }

    public static string GetDefaultDeviceName()
    {
        string cached = _cachedDefaultDeviceName;
        if (!string.IsNullOrEmpty(cached)) return cached;

        lock (_deviceLock)
        {
            try
            {
                string name = _currentDefaultDevice?.FriendlyName ?? "";
                if (!string.IsNullOrEmpty(name)) _cachedDefaultDeviceName = name;
                return name;
            }
            catch
            {
                return "";
            }
        }
    }

    public static MMDevice? GetDefaultOutputDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    public static string GetDefaultOutputDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev?.ID ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string GetDefaultInputDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return dev?.ID ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static float GetMasterVolume()
    {
        for (int retry = 0; retry < 2; retry++)
        {
            try
            {
                if (_currentDefaultDevice == null)
                {
                    AttachToDefaultDevice();
                }
                if (_currentDefaultDevice != null)
                {
                    return _currentDefaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                }
            }
            catch
            {
                AttachToDefaultDevice();
            }
        }
        return 0f;
    }

    public static bool GetIsMuted()
    {
        for (int retry = 0; retry < 2; retry++)
        {
            try
            {
                if (_currentDefaultDevice == null)
                {
                    AttachToDefaultDevice();
                }
                if (_currentDefaultDevice != null)
                {
                    return _currentDefaultDevice.AudioEndpointVolume.Mute;
                }
            }
            catch
            {
                AttachToDefaultDevice();
            }
        }
        return false;
    }

    public static float StepVolume(float deltaPercent)
    {
        for (int retry = 0; retry < 2; retry++)
        {
            try
            {
                if (_currentDefaultDevice == null)
                {
                    AttachToDefaultDevice();
                }

                var dev = _currentDefaultDevice;
                if (dev != null)
                {
                    float current = dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                    float next = Math.Clamp(current + deltaPercent, 0f, 100f);
                    dev.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;

                    if (next > 0 && dev.AudioEndpointVolume.Mute)
                    {
                        dev.AudioEndpointVolume.Mute = false;
                    }

                    return next;
                }
            }
            catch
            {
                AttachToDefaultDevice();
            }
        }
        return 0f;
    }

    public static void SetMasterVolume(float volumePercent)
    {
        for (int retry = 0; retry < 2; retry++)
        {
            try
            {
                if (_currentDefaultDevice == null)
                {
                    AttachToDefaultDevice();
                }

                var dev = _currentDefaultDevice;
                if (dev != null)
                {
                    float next = Math.Clamp(volumePercent, 0f, 100f);
                    float current = dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                    if (Math.Abs(current - next) > 0.1f)
                    {
                        dev.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;
                    }
                    return;
                }
            }
            catch
            {
                AttachToDefaultDevice();
            }
        }
    }

    /// <summary>
    /// 現在のマスター音量を指定ミリ秒かけてスムーズにフェードアウト（0%へ減衰）します。
    /// </summary>
    public static async Task FadeOutMasterVolumeAsync(int durationMs = 80, CancellationToken ct = default)
    {
        try
        {
            float startVol = GetMasterVolume();
            if (startVol <= 0.5f)
            {
                SetMasterVolume(0f);
                return;
            }

            int steps = Math.Max(4, durationMs / 10);
            int stepDelay = durationMs / steps;
            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested) break;
                float t = (float)i / steps;
                float currentVol = startVol * (1f - t * t);
                SetMasterVolume(Math.Max(0f, currentVol));
                await Task.Delay(stepDelay, ct).ConfigureAwait(false);
            }
            SetMasterVolume(0f);
        }
        catch { }
    }

    /// <summary>
    /// マスター音量を目標値へ指定ミリ秒かけてスムーズにフェードイン（復元）します。
    /// </summary>
    public static async Task FadeInMasterVolumeAsync(float targetVolume, int durationMs = 120, Action<float>? onStep = null, CancellationToken ct = default)
    {
        try
        {
            if (targetVolume <= 0.5f)
            {
                SetMasterVolume(0f);
                onStep?.Invoke(0f);
                return;
            }

            float startVol = GetMasterVolume();
            int steps = Math.Max(6, durationMs / 10);
            int stepDelay = durationMs / steps;
            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested) break;
                float t = (float)i / steps;
                float easeOut = 1f - (1f - t) * (1f - t);
                float currentVol = startVol + (targetVolume - startVol) * easeOut;
                SetMasterVolume(Math.Clamp(currentVol, 0f, 100f));
                onStep?.Invoke(currentVol);
                await Task.Delay(stepDelay, ct).ConfigureAwait(false);
            }
            SetMasterVolume(targetVolume);
            onStep?.Invoke(targetVolume);
        }
        catch { }
    }

    public static (float Volume, bool IsMuted) ToggleMute()
    {
        for (int retry = 0; retry < 2; retry++)
        {
            try
            {
                if (_currentDefaultDevice == null)
                {
                    AttachToDefaultDevice();
                }

                var dev = _currentDefaultDevice;
                if (dev != null)
                {
                    bool oldMuted = dev.AudioEndpointVolume.Mute;
                    bool newMuted = !oldMuted;
                    dev.AudioEndpointVolume.Mute = newMuted;
                    bool verifyMuted = dev.AudioEndpointVolume.Mute;
                    float vol = dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                    return (vol, verifyMuted);
                }
            }
            catch
            {
                AttachToDefaultDevice();
            }
        }
        return (GetMasterVolume(), GetIsMuted());
    }

    public static bool ToggleInputMute()
    {
        try
        {
            using var dev = GetDefaultInputDeviceInternal();
            if (dev != null)
            {
                bool newMuted = !dev.AudioEndpointVolume.Mute;
                dev.AudioEndpointVolume.Mute = newMuted;
                return newMuted;
            }
        }
        catch { }
        return false;
    }

    public static bool ToggleAllInputMute()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var devList = new List<MMDevice>();
            foreach (var d in devices)
            {
                devList.Add(d);
            }

            try
            {
                bool anyMuted = false;
                foreach (var d in devList)
                {
                    try
                    {
                        if (d.AudioEndpointVolume.Mute)
                        {
                            anyMuted = true;
                            break;
                        }
                    }
                    catch { }
                }

                bool newMuteState = !anyMuted;
                foreach (var d in devList)
                {
                    try
                    {
                        d.AudioEndpointVolume.Mute = newMuteState;
                    }
                    catch { }
                }
                return newMuteState;
            }
            finally
            {
                foreach (var d in devList)
                {
                    try { d.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"ToggleAllInputMute error: {ex.Message}");
            return ToggleInputMute();
        }
    }

    public static float GetInputVolume()
    {
        try
        {
            using var dev = GetDefaultInputDeviceInternal();
            if (dev != null)
            {
                return dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
            }
        }
        catch { }
        return 50f;
    }

    public static bool GetIsInputMuted()
    {
        try
        {
            using var dev = GetDefaultInputDeviceInternal();
            if (dev != null)
            {
                return dev.AudioEndpointVolume.Mute;
            }
        }
        catch { }
        return false;
    }

    public static void SetInputVolume(float volumePercent, string? deviceId = null)
    {
        try
        {
            float next = Math.Clamp(volumePercent, 0f, 100f);
            MMDevice? dev = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    dev = enumerator.GetDevice(deviceId);
                }
                catch { }
            }
            dev ??= GetDefaultInputDeviceInternal();

            if (dev != null)
            {
                using (dev)
                {
                    float cur = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                    if (Math.Abs(cur - (next / 100f)) > 0.005f)
                    {
                        dev.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;
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
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"SetInputVolume error: {ex.Message}");
        }
    }

    private static MMDevice? GetDefaultInputDeviceInternal()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            catch
            {
                return null;
            }
        }
    }

    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            string defaultId = _currentDefaultDevice?.ID ?? "";
            if (string.IsNullOrEmpty(defaultId))
            {
                using var def = GetDefaultOutputDevice();
                defaultId = def?.ID ?? "";
            }

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var dev in devices)
            {
                try
                {
                    list.Add(new AudioDeviceInfo
                    {
                        Id = dev.ID,
                        Name = dev.FriendlyName,
                        Volume = dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f,
                        IsMuted = dev.AudioEndpointVolume.Mute,
                        IsDefault = dev.ID == defaultId
                    });
                }
                finally
                {
                    dev.Dispose();
                }
            }
        }
        catch { }
        return list;
    }

    public static string GetEffectiveDeviceVolumeKey(string devName)
    {
        if (string.IsNullOrEmpty(devName)) return devName;

        if (devName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
        {
            var (_, fxName) = GetFxSoundOutputDevice();
            if (!string.IsNullOrEmpty(fxName))
            {
                return $"{devName} [{fxName}]";
            }
        }
        return devName;
    }

    public static (string? id, string? name) GetFxSoundOutputDevice()
    {
        try
        {
            string settingsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FxSound", "FxSound.settings");
            if (System.IO.File.Exists(settingsPath))
            {
                using var fs = new System.IO.FileStream(settingsPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                using var reader = new System.IO.StreamReader(fs);
                string xml = reader.ReadToEnd();

                string? devId = null;
                string? devName = null;

                var idMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<VALUE\s+name=""output_device_id""\s+val=""([^""]+)""");
                if (idMatch.Success && !string.IsNullOrWhiteSpace(idMatch.Groups[1].Value))
                {
                    devId = idMatch.Groups[1].Value.Trim();
                }

                var nameMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<VALUE\s+name=""output_device_name""\s+val=""([^""]+)""");
                if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                {
                    devName = System.Net.WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());
                }

                return (devId, devName);
            }
        }
        catch { }
        return (null, null);
    }

    public static void PreApplyDeviceVolume(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDevice(deviceId);
            if (dev != null)
            {
                var svSettings = SanmiToys.Core.Services.SettingsService.Instance.GetModuleSettings<SanmiToys.Modules.SwiftVolume.Models.SwiftVolumeSettings>("SwiftVolume");
                if (svSettings != null)
                {
                    string devName = dev.FriendlyName;
                    string effKey = GetEffectiveDeviceVolumeKey(devName);
                    if (svSettings.DeviceMasterVolumes.TryGetValue(effKey, out float savedVol) ||
                        (svSettings.DeviceMasterVolumes.TryGetValue(devName, out savedVol) && savedVol < 0.99f))
                    {
                        dev.AudioEndpointVolume.MasterVolumeLevelScalar = savedVol;
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 指定したデバイス（IDまたは名前）の現在のハードウェア実音量（0〜100%）を直接・高速・安全に取得する。
    /// 取得できない場合は null を返す。
    /// </summary>
    public static float? GetDeviceVolumeDirect(string? deviceId, string? deviceName = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // 1. ID指定による直接取得（最も高速・確実）
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    using var dev = enumerator.GetDevice(deviceId);
                    if (dev != null)
                    {
                        return dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                    }
                }
                catch { }
            }

            // 2. 名前照合による検索取得
            if (!string.IsNullOrEmpty(deviceName))
            {
                try
                {
                    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    foreach (var d in devices)
                    {
                        try
                        {
                            string fn = d.FriendlyName;
                            if (fn.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
                                fn.Contains(deviceName, StringComparison.OrdinalIgnoreCase) ||
                                deviceName.Contains(fn, StringComparison.OrdinalIgnoreCase))
                            {
                                return d.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                            }
                        }
                        catch { }
                        finally
                        {
                            try { d.Dispose(); } catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return null;
    }
}
