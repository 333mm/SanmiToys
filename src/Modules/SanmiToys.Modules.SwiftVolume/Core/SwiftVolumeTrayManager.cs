using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using SanmiToys.Modules.SwiftVolume.Core;
using SanmiToys.Modules.SwiftVolume.Models;
using SanmiToys.Modules.SwiftVolume.Views;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace SanmiToys.Modules.SwiftVolume.Core;

public class SwiftVolumeTrayManager : IDisposable
{
    private readonly Func<SwiftVolumeSettings> _settingsAccessor;
    private readonly Action _openSettingsAction;
    private readonly Action<float, bool>? _onVolumeChanged;

    private readonly Action<string, bool>? _onDeviceSwitched;
    private readonly DeviceEnumerationService _deviceService = new();
    private TaskbarIcon? _speakerIcon;
    private MixerWindow? _mixerWindow;
    private readonly DispatcherTimer _pollTimer;
    private int _isPolling = 0;

    private float _lastSpeakerVol = -1f;
    private bool _lastSpeakerMuted = false;
    private long _lastExplicitUpdateTicks = 0;
    private const long NOTIFICATION_DEBOUNCE_TICKS = TimeSpan.TicksPerMillisecond * 400; // 400ms
    private bool _powerEventsSubscribed;

    public SwiftVolumeTrayManager(Func<SwiftVolumeSettings> settingsAccessor, Action openSettingsAction, Action<float, bool>? onVolumeChanged = null, Action<string, bool>? onDeviceSwitched = null)
    {
        _settingsAccessor = settingsAccessor;
        _openSettingsAction = openSettingsAction;
        _onVolumeChanged = onVolumeChanged;
        _onDeviceSwitched = onDeviceSwitched;

        long _restoringUntilTicks = 0;

        AudioDeviceHelper.MasterVolumeChanged += (vol, muted) =>
        {
            try
            {
                // 復元処理中の一定時間内は、初期化通知による設定上書きをブロック
                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _restoringUntilTicks))
                {
                    return;
                }

                string devName = AudioDeviceHelper.GetDefaultDeviceName();
                if (!string.IsNullOrEmpty(devName))
                {
                    var settings = _settingsAccessor();
                    settings.DeviceMasterVolumes[devName] = vol / 100f;
                }
            }
            catch { }

            // 明示的更新後の一定時間内は、通知コールバックによる更新を抑制
            // (ToggleMute直後の古い通知による上書きを防止)
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastExplicitUpdateTicks) < NOTIFICATION_DEBOUNCE_TICKS)
                return;
            Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons(vol, muted, true));
        };

        AudioDeviceHelper.DefaultDeviceChanged += () =>
        {
            try
            {
                string devName = AudioDeviceHelper.GetDefaultDeviceName();
                if (!string.IsNullOrEmpty(devName))
                {
                    var settings = _settingsAccessor();
                    if (settings.DeviceMasterVolumes.TryGetValue(devName, out float savedVol))
                    {
                        // 復元ガードを設定（起動直後の 100% 上書きを確実に防止）
                        Interlocked.Exchange(ref _restoringUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(800).Ticks);
                        AudioDeviceHelper.SetMasterVolume(savedVol * 100f);
                        SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"Restored volume for '{devName}': {savedVol * 100f:F0}%");
                    }
                    else
                    {
                        settings.DeviceMasterVolumes[devName] = AudioDeviceHelper.GetMasterVolume() / 100f;
                    }
                }
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Error in DefaultDeviceChanged volume restore: {ex.Message}");
            }

            Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons(force: true));
        };

        // フェイルセーフ用ポーリング
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += (s, e) => PollAudioStateAsync();
    }

    private void SubscribePowerEvents()
    {
        if (_powerEventsSubscribed) return;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _powerEventsSubscribed = true;
    }

    private void UnsubscribePowerEvents()
    {
        if (!_powerEventsSubscribed) return;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _powerEventsSubscribed = false;
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            // スリープ復帰後: COM デバイスが無効になっている可能性があるため再アタッチ
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000); // デバイスが安定するまで待機
                AudioDeviceHelper.RefreshNotificationBinding();
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _lastSpeakerVol = -1f;
                    _currentIconKey = "";
                    UpdateIcons(force: true);
                });
            });
        }
    }

    private void PollAudioStateAsync()
    {
        // 先行するポーリングが実行中の場合は多重実行せずスキップ（スレッドプール滞留・フリーズを防止）
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                float vol = AudioDeviceHelper.GetMasterVolume();
                bool muted = AudioDeviceHelper.GetIsMuted();
                Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons(vol, muted, false));
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"PollAudioState error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isPolling, 0);
            }
        });
    }

    public void Start()
    {
        SubscribePowerEvents();
        var app = Application.Current;
        if (app != null)
        {
            if (app.Dispatcher.CheckAccess())
            {
                StartInternal();
            }
            else
            {
                app.Dispatcher.InvokeAsync(StartInternal);
            }
        }
    }

    private void StartInternal()
    {
        if (_mixerWindow == null)
        {
            _mixerWindow = new MixerWindow(_settingsAccessor);
        }

        InitSpeakerIcon();
        if (_speakerIcon != null)
        {
            _speakerIcon.Visibility = Visibility.Visible;
        }

        _lastSpeakerVol = -1f; // 状態キャッシュをリセット
        _pollTimer.Start();
        UpdateIcons(force: true);
    }

    public void Stop()
    {
        UnsubscribePowerEvents();

        var app = Application.Current;
        if (app != null)
        {
            if (app.Dispatcher.CheckAccess())
            {
                StopInternal();
            }
            else
            {
                app.Dispatcher.InvokeAsync(StopInternal);
            }
        }
    }

    private void StopInternal()
    {
        _pollTimer.Stop();

        if (_speakerIcon != null)
        {
            _speakerIcon.Visibility = Visibility.Collapsed;
        }

        if (_mixerWindow != null)
        {
            _mixerWindow.Hide();
        }
    }

    private void InitSpeakerIcon()
    {
        if (_speakerIcon != null)
        {
            _speakerIcon.Visibility = Visibility.Visible;
            return;
        }

        var menu = new ContextMenu();

        _speakerIcon = new TaskbarIcon
        {
            ToolTipText = "SwiftVolume",
            Visibility = Visibility.Visible
        };
        _speakerIcon.TrayLeftMouseUp += (s, e) =>
        {
            _mixerWindow?.ShowAtCursorOrTray();
        };
        _speakerIcon.TrayRightMouseUp += async (s, e) =>
        {
            await ShowContextMenuAsync(menu);
        };
        _speakerIcon.TrayMiddleMouseDown += (s, e) =>
        {
            var settings = _settingsAccessor();
            if (settings.MiddleClickMuteAll)
            {
                var (vol, muted) = AudioDeviceHelper.ToggleMute();
                UpdateIconsExplicit(vol, muted);
                _onVolumeChanged?.Invoke(vol, muted);
            }
        };
        _speakerIcon.PreviewMouseWheel += (s, e) =>
        {
            float delta = e.Delta > 0 ? 1.0f : -1.0f;
            float newVol = AudioDeviceHelper.StepVolume(delta);
            bool isMuted = AudioDeviceHelper.GetIsMuted();
            _onVolumeChanged?.Invoke(newVol, isMuted);
            UpdateIcons(newVol, isMuted, true);
        };
        try { _speakerIcon.ForceCreate(); } catch { }
    }

    private async Task ShowContextMenuAsync(ContextMenu menu)
    {
        List<SafeDeviceInfo> inputDevices;
        List<SafeDeviceInfo> outputDevices;
        try
        {
            inputDevices = await Task.Run(() => _deviceService.GetSafeInputDevices());
            outputDevices = await Task.Run(() => _deviceService.GetSafeOutputDevices());
        }
        catch
        {
            inputDevices = new List<SafeDeviceInfo>();
            outputDevices = new List<SafeDeviceInfo>();
        }

        PopulateFullContextMenu(menu, inputDevices, outputDevices);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void PopulateFullContextMenu(ContextMenu menu, List<SafeDeviceInfo> inputDevices, List<SafeDeviceInfo> outputDevices)
    {
        menu.Items.Clear();

        var loc = SanmiToys.Core.Services.LocalizationService.Instance;

        // 1. 既定のマイク 一覧
        try
        {
            if (inputDevices.Count > 0)
            {
                var micHeader = new MenuItem 
                { 
                    Header = loc.EffectiveLanguageCode == "ja" ? "既定のマイク" : "Default Microphone", 
                    IsEnabled = false, 
                    FontWeight = FontWeights.SemiBold 
                };
                menu.Items.Add(micHeader);

                foreach (var dev in inputDevices)
                {
                    var devItem = new MenuItem
                    {
                        Header = $"  {dev.Name}",
                        IsCheckable = true,
                        IsChecked = dev.IsDefault
                    };
                    string capturedId = dev.Id;
                    string capturedName = dev.Name;
                    devItem.Click += (s, e) =>
                    {
                        PolicyConfig.SetDefaultDevice(capturedId);
                        UpdateIcons(force: true);
                        _onDeviceSwitched?.Invoke(capturedName, true);
                    };
                    menu.Items.Add(devItem);
                }

                menu.Items.Add(new Separator());
            }
        }
        catch { }

        // 2. 既定のスピーカー 一覧
        try
        {
            if (outputDevices.Count > 0)
            {
                var spkHeader = new MenuItem 
                { 
                    Header = loc.EffectiveLanguageCode == "ja" ? "既定のスピーカー" : "Default Speaker", 
                    IsEnabled = false, 
                    FontWeight = FontWeights.SemiBold 
                };
                menu.Items.Add(spkHeader);

                foreach (var dev in outputDevices)
                {
                    var devItem = new MenuItem
                    {
                        Header = $"  {dev.Name}",
                        IsCheckable = true,
                        IsChecked = dev.IsDefault
                    };
                    string capturedId = dev.Id;
                    string capturedName = dev.Name;
                    devItem.Click += (s, e) =>
                    {
                        PolicyConfig.SetDefaultDevice(capturedId);
                        UpdateIcons(force: true);
                        _onDeviceSwitched?.Invoke(capturedName, false);
                    };
                    menu.Items.Add(devItem);
                }

                menu.Items.Add(new Separator());
            }
        }
        catch { }

        // 3. その他のメニュー
        var openItem = new MenuItem { Header = loc["SwiftVolume_Hotkey_OpenMixer"], FontWeight = FontWeights.SemiBold };
        openItem.Click += (s, e) => _mixerWindow?.ShowAtCursorOrTray();
        menu.Items.Add(openItem);

        var soundSettingsItem = new MenuItem { Header = loc.EffectiveLanguageCode == "ja" ? "Windows サウンド設定を開く" : "Open Windows Sound Settings" };
        soundSettingsItem.Click += (s, e) => OpenSystemUrl("ms-settings:sound");
        menu.Items.Add(soundSettingsItem);

        var volMixerItem = new MenuItem { Header = loc.EffectiveLanguageCode == "ja" ? "Windows 音量ミキサーを開く" : "Open Windows Volume Mixer" };
        volMixerItem.Click += (s, e) => OpenSystemUrl("ms-settings:apps-volume");
        menu.Items.Add(volMixerItem);

        menu.Items.Add(new Separator());

        // 4. SanmiToys を開く / 終了
        var openSanmiToysItem = new MenuItem { Header = loc["Tray_OpenDashboard"] };
        openSanmiToysItem.Click += (s, e) => _openSettingsAction();
        menu.Items.Add(openSanmiToysItem);

        var exitItem = new MenuItem { Header = loc["Tray_Exit"] };
        exitItem.Click += (s, e) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);
    }

    private static void OpenSystemUrl(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>
    /// ホットキーハンドラなどから呼ばれる明示的更新。
    /// 通知コールバックのデバウンスを設定し、遅延再検証も行う。
    /// </summary>
    public void UpdateIconsExplicit(float vol, bool muted)
    {
        Debug.WriteLine($"[SV-ICON] UpdateIconsExplicit: vol={vol}, muted={muted}");
        // 通知コールバックを一時的に抑制
        Interlocked.Exchange(ref _lastExplicitUpdateTicks, DateTime.UtcNow.Ticks);
        UpdateIcons(vol, muted, true);

        // 遅延再検証: 200ms後にデバイスから実際の状態を読み直して確定
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            try
            {
                float actualVol = AudioDeviceHelper.GetMasterVolume();
                bool actualMuted = AudioDeviceHelper.GetIsMuted();
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    Debug.WriteLine($"[SV-ICON] Re-verify: vol={actualVol}, muted={actualMuted}");
                    _lastSpeakerVol = actualVol;
                    _lastSpeakerMuted = actualMuted;
                    UpdateSpeakerIconGraphic(actualVol, actualMuted);
                });
            }
            catch { }
        });
    }

    public void UpdateIcons(float? explicitVol = null, bool? explicitMuted = null, bool force = false)
    {
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            // バックグラウンドスレッドからの呼び出し:
            // COM アクセス (GetMasterVolume/GetIsMuted) をこのスレッドで済ませてから UI スレッドへ
            float vol = explicitVol ?? AudioDeviceHelper.GetMasterVolume();
            bool muted = explicitMuted ?? AudioDeviceHelper.GetIsMuted();
            Application.Current.Dispatcher.InvokeAsync(() => UpdateIcons(vol, muted, force));
            return;
        }

        // ここは UI スレッド。explicitVol/explicitMuted は必ず渡されることを保証する
        // (UI スレッドで COM を直接呼ばないようにする)
        if (explicitVol == null || explicitMuted == null)
        {
            // 値がない場合はバックグラウンドで取得してから再呼び出し
            _ = Task.Run(() =>
            {
                try
                {
                    float vol = AudioDeviceHelper.GetMasterVolume();
                    bool muted = AudioDeviceHelper.GetIsMuted();
                    Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons(vol, muted, force));
                }
                catch { }
            });
            return;
        }

        try
        {
            float vol = explicitVol.Value;
            bool isSpkMuted = explicitMuted.Value;

            if (force || Math.Abs(_lastSpeakerVol - vol) > 0.5f || _lastSpeakerMuted != isSpkMuted)
            {
                _lastSpeakerVol = vol;
                _lastSpeakerMuted = isSpkMuted;
                UpdateSpeakerIconGraphic(vol, isSpkMuted);
            }
        }
        catch { }
    }


    private string _currentIconKey = "";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Drawing.Icon> _iconCache = new();

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var val = key.GetValue("SystemUsesLightTheme");
                if (val is int lightTheme)
                {
                    return lightTheme == 0;
                }
            }
        }
        catch { }
        return true; // 既定はダークテーマ
    }

    private void UpdateSpeakerIconGraphic(float volume, bool isMuted)
    {
        if (_speakerIcon == null) return;

        // 0.0〜1.0スケールが渡された場合も0〜100%に正規化
        float vol = (volume > 0f && volume <= 1.0f) ? volume * 100f : volume;

        int stage = 0;
        if (vol > 0 && !isMuted)
        {
            if (vol <= 33.3f) stage = 1;
            else if (vol <= 66.6f) stage = 2;
            else stage = 3;
        }

        string prefix = IsSystemDarkTheme() ? "spk_white" : "spk_dark";
        string cacheKey = isMuted ? $"{prefix}_0" : $"{prefix}_{stage}";

        // 同じアイコンキーの場合は不要な更新をスキップ
        if (cacheKey == _currentIconKey && _speakerIcon.Icon != null)
        {
            _speakerIcon.ToolTipText = $"SwiftVolume - 音量: {(int)vol}%{(isMuted ? " (ミュート)" : "")}";
            return;
        }

        _currentIconKey = cacheKey;

        var icon = LoadOriginalSpeakerIcon(cacheKey);
        if (icon != null)
        {
            _speakerIcon.Icon = null;
            _speakerIcon.Icon = icon;
            _speakerIcon.ToolTipText = $"SwiftVolume - 音量: {(int)vol}%{(isMuted ? " (ミュート)" : "")}";
            Debug.WriteLine($"[SV-ICON] Set: {cacheKey}");
        }
    }

    private static System.Drawing.Icon? LoadOriginalSpeakerIcon(string cacheKey)
    {
        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
        {
            return cachedIcon;
        }

        try
        {
            string iconPath = $"/SanmiToys.Modules.SwiftVolume;component/Icons/{cacheKey}.png";
            var uri = new Uri($"pack://application:,,,{iconPath}", UriKind.Absolute);

            var resourceInfo = Application.GetResourceStream(uri);
            if (resourceInfo == null) return null;

            using var stream = resourceInfo.Stream;
            using var origBitmap = new System.Drawing.Bitmap(stream);

            using var resizedBitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(resizedBitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(origBitmap, 0, 0, 32, 32);
            }

            using var pngMs = new MemoryStream();
            resizedBitmap.Save(pngMs, System.Drawing.Imaging.ImageFormat.Png);
            byte[] pngBytes = pngMs.ToArray();

            var icon = CreateIconFromPng(pngBytes, 32, 32);
            if (icon != null)
            {
                _iconCache[cacheKey] = icon;
                return icon;
            }
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Failed to load icon {cacheKey}: {ex.Message}");
        }

        return null;
    }

    private static System.Drawing.Icon? CreateIconFromPng(byte[] pngBytes, int width, int height)
    {
        try
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // ICONHEADER (6 bytes)
            bw.Write((short)0); // Reserved
            bw.Write((short)1); // Type 1 = ICO
            bw.Write((short)1); // Image count = 1

            // ICONDIRENTRY (16 bytes)
            bw.Write((byte)(width == 256 ? 0 : width));   // Width
            bw.Write((byte)(height == 256 ? 0 : height)); // Height
            bw.Write((byte)0);  // Color count (0 = >=8bpp)
            bw.Write((byte)0);  // Reserved
            bw.Write((short)1); // Color planes
            bw.Write((short)32);// Bits per pixel
            bw.Write((int)pngBytes.Length); // Image data size
            bw.Write((int)22);  // Offset of image data (6 + 16 = 22)

            // Image data (PNG format is valid inside ICO container since Windows Vista)
            bw.Write(pngBytes);
            bw.Flush();

            ms.Position = 0;
            return new System.Drawing.Icon(ms, width, height);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _deviceService.Dispose();
    }
}
