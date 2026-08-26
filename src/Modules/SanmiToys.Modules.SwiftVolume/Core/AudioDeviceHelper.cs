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
    private static readonly MMDeviceEnumerator _enumerator = new();
    private static MMDevice? _currentDefaultDevice;
    private static AudioEndpointVolumeNotificationDelegate? _volumeNotificationHandler;
    private static readonly AudioNotificationClient _notificationClient = new();
    private static readonly object _deviceLock = new();
    private static System.Threading.CancellationTokenSource? _debounceCts;
    private static readonly object _debounceLock = new();

    public static event Action<float, bool>? MasterVolumeChanged;
    public static event Action? DefaultDeviceChanged;

    static AudioDeviceHelper()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            AttachToDefaultDevice();
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Error("SwiftVolume", "Failed to register endpoint notification callback", ex);
        }
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
            lock (_debounceLock)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new System.Threading.CancellationTokenSource();
                var token = _debounceCts.Token;

                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        // 連続変更が落ち着くまで 80ms デバウンス待機（多重発火の嵐を遮断）
                        await System.Threading.Tasks.Task.Delay(80, token);
                        if (token.IsCancellationRequested) return;

                        PerformDeviceSync();

                        // 外部仮想オーディオデバイス（FxSound 等）の切り替え完了待機（150ms 後の追従）
                        await System.Threading.Tasks.Task.Delay(150, token);
                        if (token.IsCancellationRequested) return;

                        PerformDeviceSync();
                    }
                    catch (System.OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        SanmiToys.Core.Services.AppLogger.Error("SwiftVolume", "Error in ScheduleDeviceChange", ex);
                    }
                }, token);
            }
        }

        private static void PerformDeviceSync()
        {
            try
            {
                AttachToDefaultDevice();
                DefaultDeviceChanged?.Invoke();
                float vol = GetMasterVolume();
                bool muted = GetIsMuted();
                MasterVolumeChanged?.Invoke(vol, muted);
                SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"Default audio device synced: {GetDefaultDeviceName()} (Vol: {vol:F0}%, Muted: {muted})");
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Error during audio device sync: {ex.Message}");
            }
        }
    }

    private static void AttachToDefaultDevice()
    {
        lock (_deviceLock)
        {
            try
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

                _currentDefaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (_currentDefaultDevice != null)
                {
                    _volumeNotificationHandler = (data) =>
                    {
                        MasterVolumeChanged?.Invoke(data.MasterVolume * 100f, data.Muted);
                    };
                    _currentDefaultDevice.AudioEndpointVolume.OnVolumeNotification += _volumeNotificationHandler;
                }
            }
            catch (Exception ex)
            {
                _currentDefaultDevice = null;
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"AttachToDefaultDevice warning: {ex.Message}");
            }
        }
    }

    public static void RefreshNotificationBinding()
    {
        AttachToDefaultDevice();
        DefaultDeviceChanged?.Invoke();
    }

    public static string GetDefaultDeviceName()
    {
        lock (_deviceLock)
        {
            try
            {
                return _currentDefaultDevice?.FriendlyName ?? "";
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
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
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
                    dev.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;
                    return;
                }
            }
            catch
            {
                AttachToDefaultDevice();
            }
        }
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
            using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
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

            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
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
}
