using System;
using System.Threading;
using System.Threading.Tasks;
using SanmiToys.Modules.SwiftVolume.Models;

namespace SanmiToys.Modules.SwiftVolume.Helpers;

public static class SwiftVolumeSettingsHelper
{
    private static readonly object _saveLock = new();
    private static CancellationTokenSource? _saveCts;

    public static void SaveSettingsDebounced(SwiftVolumeSettings settings, int delayMs = 600)
    {
        lock (_saveLock)
        {
            try
            {
                _saveCts?.Cancel();
                _saveCts?.Dispose();
            }
            catch { }

            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (!token.IsCancellationRequested)
                    {
                        SanmiToys.Core.Services.SettingsService.Instance.SetModuleSettings("SwiftVolume", settings);
                    }
                }
                catch { }
            }, token);
        }
    }

    public static void SaveSettingsImmediately(SwiftVolumeSettings settings)
    {
        lock (_saveLock)
        {
            try
            {
                _saveCts?.Cancel();
                _saveCts?.Dispose();
                _saveCts = null;
            }
            catch { }

            try
            {
                SanmiToys.Core.Services.SettingsService.Instance.SetModuleSettings("SwiftVolume", settings);
            }
            catch { }
        }
    }
}
