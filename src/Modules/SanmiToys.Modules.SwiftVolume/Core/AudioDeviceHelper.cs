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

    public static event Action<float, bool>? MasterVolumeChanged;
    public static event Action? DefaultDeviceChanged;

    static AudioDeviceHelper()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            AttachToDefaultDevice();
        }
        catch { }
    }

    private class AudioNotificationClient : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
        {
            if (dataFlow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
            {
                AttachToDefaultDevice();
                DefaultDeviceChanged?.Invoke();
                try
                {
                    float vol = GetMasterVolume();
                    bool muted = GetIsMuted();
                    MasterVolumeChanged?.Invoke(vol, muted);
                }
                catch { }
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            AttachToDefaultDevice();
            DefaultDeviceChanged?.Invoke();
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private static void AttachToDefaultDevice()
    {
        lock (_enumerator)
        {
            try
            {
                if (_currentDefaultDevice != null && _volumeNotificationHandler != null)
                {
                    try { _currentDefaultDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeNotificationHandler; } catch { }
                    try { _currentDefaultDevice.Dispose(); } catch { }
                    _currentDefaultDevice = null;
                }

                _currentDefaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (_currentDefaultDevice != null)
                {
                    _volumeNotificationHandler = (data) =>
                    {
                        System.Diagnostics.Trace.WriteLine($"[SV-AUDIO] OnVolumeNotification: vol={data.MasterVolume * 100f}, muted={data.Muted}");
                        MasterVolumeChanged?.Invoke(data.MasterVolume * 100f, data.Muted);
                    };
                    _currentDefaultDevice.AudioEndpointVolume.OnVolumeNotification += _volumeNotificationHandler;
                }
            }
            catch { }
        }
    }

    public static void RefreshNotificationBinding()
    {
        AttachToDefaultDevice();
        DefaultDeviceChanged?.Invoke();
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
        try
        {
            if (_currentDefaultDevice != null)
            {
                return _currentDefaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
            }
            using var dev = GetDefaultOutputDevice();
            return dev?.AudioEndpointVolume.MasterVolumeLevelScalar * 100f ?? 0f;
        }
        catch
        {
            try
            {
                AttachToDefaultDevice();
                return _currentDefaultDevice?.AudioEndpointVolume.MasterVolumeLevelScalar * 100f ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }

    public static bool GetIsMuted()
    {
        try
        {
            if (_currentDefaultDevice != null)
            {
                return _currentDefaultDevice.AudioEndpointVolume.Mute;
            }
            using var dev = GetDefaultOutputDevice();
            return dev?.AudioEndpointVolume.Mute ?? false;
        }
        catch
        {
            try
            {
                AttachToDefaultDevice();
                return _currentDefaultDevice?.AudioEndpointVolume.Mute ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    public static float StepVolume(float deltaPercent)
    {
        try
        {
            var dev = _currentDefaultDevice;
            if (dev == null)
            {
                AttachToDefaultDevice();
                dev = _currentDefaultDevice;
            }

            if (dev == null) return 0f;

            float current = dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
            float next = Math.Clamp(current + deltaPercent, 0f, 100f);
            dev.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;

            if (next > 0 && dev.AudioEndpointVolume.Mute)
            {
                dev.AudioEndpointVolume.Mute = false;
            }

            return next;
        }
        catch
        {
            AttachToDefaultDevice();
            return 0f;
        }
    }

    public static void SetMasterVolume(float volumePercent)
    {
        try
        {
            var dev = _currentDefaultDevice;
            if (dev == null)
            {
                AttachToDefaultDevice();
                dev = _currentDefaultDevice;
            }

            if (dev != null)
            {
                float next = Math.Clamp(volumePercent, 0f, 100f);
                dev.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;
            }
        }
        catch
        {
            AttachToDefaultDevice();
        }
    }

    public static (float Volume, bool IsMuted) ToggleMute()
    {
        try
        {
            var dev = _currentDefaultDevice;
            if (dev == null)
            {
                AttachToDefaultDevice();
                dev = _currentDefaultDevice;
            }

            if (dev != null)
            {
                bool oldMuted = dev.AudioEndpointVolume.Mute;
                bool newMuted = !oldMuted;
                dev.AudioEndpointVolume.Mute = newMuted;
                // 設定後に再読み取りして確認
                bool verifyMuted = dev.AudioEndpointVolume.Mute;
                float vol = dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                System.Diagnostics.Trace.WriteLine($"[SV-AUDIO] ToggleMute: old={oldMuted}, set={newMuted}, verify={verifyMuted}, vol={vol}");
                return (vol, verifyMuted);
            }
        }
        catch
        {
            AttachToDefaultDevice();
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
