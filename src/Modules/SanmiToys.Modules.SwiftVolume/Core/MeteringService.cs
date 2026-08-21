using System;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;

namespace SanmiToys.Modules.SwiftVolume.Core;

public class MeteringService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ConcurrentDictionary<string, MMDevice> _cachedDevices = new();

    private MMDevice? GetCachedDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        return _cachedDevices.GetOrAdd(deviceId, id =>
        {
            try { return _enumerator.GetDevice(id); }
            catch { return null!; }
        });
    }

    public float GetPeakLevel(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return 0f;
        try
        {
            var dev = GetCachedDevice(deviceId);
            if (dev?.AudioMeterInformation == null) return 0f;
            return dev.AudioMeterInformation.MasterPeakValue;
        }
        catch
        {
            return 0f;
        }
    }

    public float GetDefaultOutputPeakLevel()
    {
        try
        {
            using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev?.AudioMeterInformation?.MasterPeakValue ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    public float GetDefaultInputPeakLevel()
    {
        try
        {
            using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return dev?.AudioMeterInformation?.MasterPeakValue ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    public void InvalidateCache()
    {
        foreach (var dev in _cachedDevices.Values)
        {
            try { dev?.Dispose(); } catch { }
        }
        _cachedDevices.Clear();
    }

    public void Dispose()
    {
        InvalidateCache();
        _enumerator.Dispose();
    }
}
