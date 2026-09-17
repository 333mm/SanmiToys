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

    /// <summary>
    /// 除外対象アプリ（FxSound等）の過去に保存された音量設定キーをパージする
    /// </summary>
    public static bool PurgeExcludedAppVolumes(SwiftVolumeSettings settings)
    {
        if (settings.AppVolumes == null || settings.AppVolumes.Count == 0) return false;
        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var kvp in settings.AppVolumes)
        {
            foreach (var ex in SanmiToys.Modules.SwiftVolume.Core.DeviceEnumerationService.ExcludedProcessNames)
            {
                if (kvp.Key.Contains(ex, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(kvp.Key);
                    break;
                }
            }
        }

        if (keysToRemove.Count > 0)
        {
            foreach (var k in keysToRemove)
            {
                settings.AppVolumes.Remove(k);
            }
            SaveSettingsImmediately(settings);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 過去の二重管理音量情報（AppVolumes, DeviceMasterVolumes）をクリーンリセットする
    /// </summary>
    public static void ResetAllVolumeData(SwiftVolumeSettings settings)
    {
        if (settings.VolumeDataResetV2) return;

        settings.AppVolumes?.Clear();
        settings.DeviceMasterVolumes?.Clear();
        settings.VolumeDataResetV2 = true;
        SaveSettingsImmediately(settings);
    }
}
