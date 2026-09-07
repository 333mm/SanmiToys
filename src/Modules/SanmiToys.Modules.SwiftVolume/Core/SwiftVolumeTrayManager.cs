using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using SanmiToys.Core.Services;
using SanmiToys.Modules.SwiftVolume.Core;
using SanmiToys.Modules.SwiftVolume.Helpers;
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
    private readonly Action<bool>? _onMicMuteChanged;
    private readonly DeviceEnumerationService _deviceService = new();
    private TaskbarIcon? _speakerIcon;
    private TaskbarIcon? _micIcon;
    private MixerWindow? _mixerWindow;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _micPollTimer;
    private int _isPolling = 0;
    private int _isMicPolling = 0;
    private readonly MeteringService _meteringService = new();

    private float _lastSpeakerVol = -1f;
    private bool _lastSpeakerMuted = false;
    private string _currentMicIconKey = "";
    private bool _lastMicMuted = false;
    private bool _lastMicActive = false;
    private long _lastMicActiveTicks = 0;
    private long _lastExplicitUpdateTicks = 0;
    private const long NOTIFICATION_DEBOUNCE_TICKS = TimeSpan.TicksPerMillisecond * 400; // 400ms
    private bool _powerEventsSubscribed;
    private volatile bool _isSuspended = false;
    private long _restoringUntilTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(5).Ticks;
    private List<SafeDeviceInfo> _cachedInputDevices = new();
    private List<SafeDeviceInfo> _cachedOutputDevices = new();
    private System.IO.FileSystemWatcher? _fxSoundWatcher;
    private string? _lastFxSoundOutputId;
    private string? _lastFxSoundOutputName;
    private int _isApplyingFxSoundChange = 0;

    public static readonly Guid SpeakerTrayIconGuid = new("8A426B9C-7F12-4DF6-9B37-123456789ABC");
    public static readonly Guid MicTrayIconGuid = new("C5B8B77E-9721-4E11-9CF4-21950F8559D2");

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect([In] ref NOTIFYICONIDENTIFIER identifier, [Out] out RECT iconLocation);

    private long _lastSpeakerMouseMoveTick = 0;
    private int _lastSpeakerMouseX = 0;
    private int _lastSpeakerMouseY = 0;

    public SwiftVolumeTrayManager(Func<SwiftVolumeSettings> settingsAccessor, Action openSettingsAction, Action<float, bool>? onVolumeChanged = null, Action<string, bool>? onDeviceSwitched = null, Action<bool>? onMicMuteChanged = null)
    {
        _settingsAccessor = settingsAccessor;
        _openSettingsAction = openSettingsAction;
        _onVolumeChanged = onVolumeChanged;
        _onDeviceSwitched = onDeviceSwitched;
        _onMicMuteChanged = onMicMuteChanged;

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
                    string key = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(devName);
                    var settings = _settingsAccessor();
                    settings.DeviceMasterVolumes[key] = vol / 100f;
                    SwiftVolumeSettingsHelper.SaveSettingsDebounced(settings);
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
                    string key = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(devName);
                    var settings = _settingsAccessor();
                    if (settings.DeviceMasterVolumes.TryGetValue(key, out float savedVol) ||
                        (settings.DeviceMasterVolumes.TryGetValue(devName, out savedVol) && savedVol < 0.99f))
                    {
                        // 復元ガードを設定（起動直後の 100% 上書きを確実に防止）
                        Interlocked.Exchange(ref _restoringUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(800).Ticks);
                        AudioDeviceHelper.SetMasterVolume(savedVol * 100f);
                        SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"Restored volume for '{key}': {savedVol * 100f:F0}%");
                    }
                    else
                    {
                        settings.DeviceMasterVolumes[key] = AudioDeviceHelper.GetMasterVolume() / 100f;
                        SwiftVolumeSettingsHelper.SaveSettingsDebounced(settings);
                    }
                }
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Error in DefaultDeviceChanged volume restore: {ex.Message}");
            }

            Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons(force: true));
            ApplyAllAppVolumesAsync();
        };

        // フェイルセーフ用ポーリング
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += (s, e) => PollAudioStateAsync();

        // マイク状態・発光検知用ポーリング
        _micPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _micPollTimer.Tick += (s, e) => PollMicAudioStateAsync();

        StartFxSoundWatcher();
    }

    private void StartFxSoundWatcher()
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FxSound");
            if (System.IO.Directory.Exists(dir))
            {
                _fxSoundWatcher = new System.IO.FileSystemWatcher(dir, "FxSound.settings")
                {
                    NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                void OnWatcherTriggered(object s, FileSystemEventArgs e)
                {
                    _ = ApplyFxSoundDeviceChangeAsync();
                }

                _fxSoundWatcher.Changed += OnWatcherTriggered;
                _fxSoundWatcher.Created += OnWatcherTriggered;
                _fxSoundWatcher.Renamed += (s, e) => OnWatcherTriggered(s, e);
            }
        }
        catch { }
    }

    private async Task ApplyFxSoundDeviceChangeAsync(bool force = false)
    {
        if (Interlocked.CompareExchange(ref _isApplyingFxSoundChange, 1, 0) != 0) return;
        try
        {
            // FxSound のファイル書き込み完了を少し待機
            await Task.Delay(150);

            string defName = AudioDeviceHelper.GetDefaultDeviceName();
            if (!defName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var (newFxId, newFxName) = AudioDeviceHelper.GetFxSoundOutputDevice();
            if (string.IsNullOrEmpty(newFxId) && string.IsNullOrEmpty(newFxName))
            {
                return;
            }

            // デバイスに変更があったか確認（force でなければ同デバイスへの重複処理をスキップ）
            if (!force &&
                string.Equals(_lastFxSoundOutputId, newFxId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_lastFxSoundOutputName, newFxName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastFxSoundOutputId = newFxId;
            _lastFxSoundOutputName = newFxName;

            var settings = _settingsAccessor();
            float targetVol = -1f;

            // 1. 保存音量の取得 (優先度1: FxSound [裏デバイス名] の実効キー)
            string effKey = !string.IsNullOrEmpty(newFxName) ? $"{defName} [{newFxName}]" : AudioDeviceHelper.GetEffectiveDeviceVolumeKey(defName);
            if (settings.DeviceMasterVolumes.TryGetValue(effKey, out float savedEffVol))
            {
                targetVol = savedEffVol * 100f;
            }
            // 優先度2: 裏デバイス名単体キー
            else if (!string.IsNullOrEmpty(newFxName) && settings.DeviceMasterVolumes.TryGetValue(newFxName, out float savedDevVol))
            {
                targetVol = savedDevVol * 100f;
            }
            // 優先度3: 裏デバイスの実ハードウェア音量
            else
            {
                var outs = _deviceService.GetSafeOutputDevices();
                var matched = outs.FirstOrDefault(d =>
                    (!string.IsNullOrEmpty(newFxId) && d.Id.Equals(newFxId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(newFxName) && d.Name.Contains(newFxName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(newFxName) && newFxName.Contains(d.Name, StringComparison.OrdinalIgnoreCase)));
                if (matched != null)
                {
                    targetVol = matched.Volume * 100f;
                }
            }

            if (targetVol < 0)
            {
                targetVol = AudioDeviceHelper.GetMasterVolume();
            }

            if (targetVol >= 0)
            {
                Interlocked.Exchange(ref _restoringUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(1500).Ticks);

                // ① 新裏デバイス自体に即座に音量を適用！
                var outs = _deviceService.GetSafeOutputDevices();
                var targetDevice = outs.FirstOrDefault(d =>
                    (!string.IsNullOrEmpty(newFxId) && d.Id.Equals(newFxId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(newFxName) && d.Name.Contains(newFxName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(newFxName) && newFxName.Contains(d.Name, StringComparison.OrdinalIgnoreCase)));

                if (targetDevice != null)
                {
                    _deviceService.SetDeviceVolumeDirect(targetDevice.Id, targetVol);
                }
                else if (!string.IsNullOrEmpty(newFxId))
                {
                    _deviceService.SetDeviceVolumeDirect(newFxId, targetVol);
                }

                // ② 既定デバイス（FxSound）のマスター音量にも即座に適用！
                AudioDeviceHelper.SetMasterVolume(targetVol);

                // ③ 設定へも同期保存
                settings.DeviceMasterVolumes[effKey] = targetVol / 100f;
                if (!string.IsNullOrEmpty(newFxName))
                {
                    settings.DeviceMasterVolumes[newFxName] = targetVol / 100f;
                }
                SwiftVolumeSettingsHelper.SaveSettingsDebounced(settings);

                // ④ トレイアイコンとHUDへ即座に通知
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    UpdateIcons(targetVol, false, true);
                    _onVolumeChanged?.Invoke(targetVol, false);
                });

                // ⑤ MixerWindow が開いていれば UI も即座に再描画・同期
                if (_mixerWindow != null)
                {
                    await _mixerWindow.Dispatcher.InvokeAsync(() =>
                    {
                        if (_mixerWindow.IsVisible)
                        {
                            _mixerWindow.RefreshDataAsync();
                        }
                    });
                }
            }

            ApplyAllAppVolumesAsync();
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"ApplyFxSoundDeviceChange error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isApplyingFxSoundChange, 0);
        }
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
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            _isSuspended = true;
            _pollTimer.Stop();
            _micPollTimer.Stop();
            SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", "System suspending: paused audio polling and COM access");
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", "System resuming: scheduling safe COM reinitialization");
            // スリープ復帰後: Windows Audio サービスとドライバが安定するまで待機してから安全に再初期化
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000); // デバイスとAudioSrvが安定するまで十分に待機
                _isSuspended = false;

                try
                {
                    AudioDeviceHelper.Reinitialize();
                }
                catch (Exception ex)
                {
                    SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Reinitialize error on resume: {ex.Message}");
                }

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _lastSpeakerVol = -1f;
                    _currentIconKey = "";
                    _currentMicIconKey = "";
                    UpdateIcons(force: true);
                    UpdateMicIcon(force: true);
                    _pollTimer.Start();
                    _micPollTimer.Start();
                });

                RestoreAllDeviceVolumesAsync();
                ApplyAllAppVolumesAsync();
            });
        }
    }

    private void PollAudioStateAsync()
    {
        if (_isSuspended) return;

        // 先行するポーリングが実行中の場合は多重実行せずスキップ（スレッドプール滞留・フリーズを防止）
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            if (_isSuspended)
            {
                Interlocked.Exchange(ref _isPolling, 0);
                return;
            }

            try
            {
                float vol = AudioDeviceHelper.GetMasterVolume();
                bool muted = AudioDeviceHelper.GetIsMuted();
                Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons(vol, muted, false));

                // FxSound を既定で使用中の場合、出力先裏デバイスの変化を常時二重検知
                string defName = AudioDeviceHelper.GetDefaultDeviceName();
                if (defName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
                {
                    var (currFxId, currFxName) = AudioDeviceHelper.GetFxSoundOutputDevice();
                    if (!string.IsNullOrEmpty(currFxId) &&
                        (!string.Equals(_lastFxSoundOutputId, currFxId, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(_lastFxSoundOutputName, currFxName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = ApplyFxSoundDeviceChangeAsync();
                    }
                }
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

    private void PollMicAudioStateAsync()
    {
        if (_isSuspended) return;
        var settings = _settingsAccessor();
        if (!settings.ShowMicTrayIcon) return;

        if (Interlocked.CompareExchange(ref _isMicPolling, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            if (_isSuspended)
            {
                Interlocked.Exchange(ref _isMicPolling, 0);
                return;
            }

            try
            {
                bool isMuted = AudioDeviceHelper.GetIsInputMuted();
                float peak = 0f;
                if (settings.EnableMicGlow && !isMuted)
                {
                    peak = _meteringService.GetDefaultInputPeakLevel();
                }

                long now = Environment.TickCount64;
                if (peak > 0.05f)
                {
                    Interlocked.Exchange(ref _lastMicActiveTicks, now);
                }

                // 発光ホールド: 検知後約300msはactiveを維持（点滅防止）
                bool isActive = (now - Interlocked.Read(ref _lastMicActiveTicks)) < 300;

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    UpdateMicIcon(isMuted, isActive, false);
                });
            }
            catch (Exception ex)
            {
                SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"PollMicAudioState error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isMicPolling, 0);
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

        InitMicIcon();

        _lastSpeakerVol = -1f; // 状態キャッシュをリセット
        _currentMicIconKey = "";
        _pollTimer.Start();
        _micPollTimer.Start();
        UpdateIcons(force: true);
        UpdateMicIcon(force: true);

        // 起動時の音量復元（全出力デバイスおよび既定デバイス/FxSoundの保存音量を確実に復元）
        RestoreAllDeviceVolumesAsync();
        StartFxSoundStartupWatchdog();

        // コントロールパネルを開かなくても起動時点で全アプリの音量設定を反映
        ApplyAllAppVolumesAsync();

        // コンテキストメニュー用デバイス情報を事前キャッシュ
        _ = RefreshCachedDevicesAsync();
    }

    private void RestoreAllDeviceVolumesAsync()
    {
        Task.Run(async () =>
        {
            // 起動直後の Windows / ドライバ初期化通知による設定上書きを確実に防ぐため復元ガードを設定
            Interlocked.Exchange(ref _restoringUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(5000).Ticks);

            int[] delays = { 100, 600, 1500, 3500 };
            foreach (var delay in delays)
            {
                await Task.Delay(delay);
                try
                {
                    var settings = _settingsAccessor();
                    var outputDevices = _deviceService.GetSafeOutputDevices();
                    if (outputDevices.Count == 0) continue;

                    // 1. 各出力デバイス（裏デバイスを含む）の保存音量を復元
                    foreach (var dev in outputDevices)
                    {
                        string devKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(dev.Name);
                        if (settings.DeviceMasterVolumes.TryGetValue(devKey, out float savedVol) ||
                            settings.DeviceMasterVolumes.TryGetValue(dev.Name, out savedVol))
                        {
                            float curVol = dev.Volume;
                            if (Math.Abs(curVol - savedVol) > 0.01f)
                            {
                                _deviceService.SetDeviceVolumeDirect(dev.Id, savedVol * 100f);
                                dev.Volume = savedVol;
                                SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"Restored volume for device '{dev.Name}': {savedVol * 100f:F0}%");
                            }
                        }
                    }

                    // 2. 既定デバイス（特に FxSound）のマスター音量を復元
                    string defName = AudioDeviceHelper.GetDefaultDeviceName();
                    if (!string.IsNullOrEmpty(defName))
                    {
                        string defKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(defName);
                        if (settings.DeviceMasterVolumes.TryGetValue(defKey, out float savedVol) ||
                            (settings.DeviceMasterVolumes.TryGetValue(defName, out savedVol) && savedVol < 0.99f))
                        {
                            float curMaster = AudioDeviceHelper.GetMasterVolume() / 100f;
                            if (Math.Abs(curMaster - savedVol) > 0.01f)
                            {
                                AudioDeviceHelper.SetMasterVolume(savedVol * 100f);
                                SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"Restored master volume for '{defKey}': {savedVol * 100f:F0}%");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"RestoreAllDeviceVolumesAsync error: {ex.Message}");
                }
            }
        });
    }

    private void TryRestoreDeviceVolume(bool forceFxSoundOnly = false)
    {
        try
        {
            string devName = AudioDeviceHelper.GetDefaultDeviceName();
            if (string.IsNullOrEmpty(devName)) return;

            bool isFxSound = devName.Contains("FxSound", StringComparison.OrdinalIgnoreCase);
            if (forceFxSoundOnly && !isFxSound) return;

            string key = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(devName);
            var settings = _settingsAccessor();
            if (settings.DeviceMasterVolumes.TryGetValue(key, out float savedVol) ||
                (settings.DeviceMasterVolumes.TryGetValue(devName, out savedVol) && savedVol < 0.99f))
            {
                float currentVol = AudioDeviceHelper.GetMasterVolume() / 100f;
                if (isFxSound || Math.Abs(currentVol - savedVol) > 0.02f)
                {
                    Interlocked.Exchange(ref _restoringUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(isFxSound ? 2500 : 800).Ticks);
                    AudioDeviceHelper.SetMasterVolume(savedVol * 100f);
                    SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"Startup volume accurately restored for '{key}': {savedVol * 100f:F0}%");
                }
            }
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"TryRestoreDeviceVolume error: {ex.Message}");
        }
    }

    private void StartFxSoundStartupWatchdog()
    {
        // Windows再起動時やアプリ再起動時の FxSound 遅延起動・音量リセットを監視し、設定音量を確実に維持
        Task.Run(async () =>
        {
            int[] delays = { 500, 1200, 2500, 4500, 7500 };
            foreach (var delay in delays)
            {
                await Task.Delay(delay);
                try
                {
                    string defName = AudioDeviceHelper.GetDefaultDeviceName();
                    if (!string.IsNullOrEmpty(defName) && defName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
                    {
                        string key = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(defName);
                        var settings = _settingsAccessor();
                        if (settings.DeviceMasterVolumes.TryGetValue(key, out float savedVol) ||
                            settings.DeviceMasterVolumes.TryGetValue(defName, out savedVol))
                        {
                            float curVol = AudioDeviceHelper.GetMasterVolume() / 100f;
                            if (Math.Abs(curVol - savedVol) > 0.02f)
                            {
                                Interlocked.Exchange(ref _restoringUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(2000).Ticks);
                                AudioDeviceHelper.SetMasterVolume(savedVol * 100f);
                                SanmiToys.Core.Services.AppLogger.Info("SwiftVolume", $"FxSound startup watchdog reapplied volume for '{key}': {savedVol * 100f:F0}%");
                            }
                        }
                    }
                }
                catch { }
            }
        });
    }

    private void ApplyAllAppVolumesAsync()
    {
        // コントロールパネルを開かなくても、タスクトレイ起動時点およびデバイス切替時に各アプリの音量設定を即時反映
        Task.Run(async () =>
        {
            int[] delays = { 200, 1500, 4000 };
            foreach (var delay in delays)
            {
                await Task.Delay(delay);
                try
                {
                    var settings = _settingsAccessor();
                    var outs = _deviceService.GetSafeOutputDevices();
                    var def = outs.FirstOrDefault(d => d.IsDefault) ?? outs.FirstOrDefault();
                    if (def == null) continue;

                    var sessions = _deviceService.GetSafeSessions(def.Id);
                    string currentDevName = def.Name;
                    string effectiveDevKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(currentDevName);

                    foreach (var session in sessions)
                    {
                        string volKey = $"{effectiveDevKey}_{session.DisplayName}";
                        string legacyVolKey = $"{currentDevName}_{session.DisplayName}";
                        bool hasSavedVol = false;
                        float targetVol = 0f;

                        if (settings.AppVolumes.TryGetValue(volKey, out float savedVol) ||
                            settings.AppVolumes.TryGetValue(legacyVolKey, out savedVol))
                        {
                            targetVol = savedVol;
                            hasSavedVol = true;
                        }
                        else if (!session.DisplayName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
                        {
                            var fallbackEntry = settings.AppVolumes.FirstOrDefault(kvp => kvp.Key.EndsWith($"_{session.DisplayName}", StringComparison.OrdinalIgnoreCase));
                            if (!string.IsNullOrEmpty(fallbackEntry.Key))
                            {
                                targetVol = fallbackEntry.Value;
                                hasSavedVol = true;
                            }
                        }

                        if (!hasSavedVol)
                        {
                            if (session.DisplayName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
                            {
                                // 未登録の FxSound セッションは変更しない
                                continue;
                            }
                            targetVol = Math.Clamp(settings.DefaultAppVolumePercent / 100.0f, 0.0f, 1.0f);
                        }

                        session.Volume = targetVol;
                        var ctrls = session.Controls.Count > 0 ? session.Controls : (session.Control != null ? new List<NAudio.CoreAudioApi.AudioSessionControl> { session.Control } : new List<NAudio.CoreAudioApi.AudioSessionControl>());
                        foreach (var ctrl in ctrls)
                        {
                            try
                            {
                                if (Math.Abs(ctrl.SimpleAudioVolume.Volume - targetVol) > 0.01f)
                                {
                                    ctrl.SimpleAudioVolume.Volume = targetVol;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"ApplyAllAppVolumesAsync error: {ex.Message}");
                }
            }
        });
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
        _micPollTimer.Stop();

        if (_speakerIcon != null)
        {
            _speakerIcon.Visibility = Visibility.Collapsed;
        }

        if (_micIcon != null)
        {
            _micIcon.Visibility = Visibility.Collapsed;
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
        EnableDismissOnOutsideClick(menu);

        _speakerIcon = new TaskbarIcon
        {
            Id = SpeakerTrayIconGuid,
            ToolTipText = "SwiftVolume",
            Visibility = Visibility.Visible
        };
        _speakerIcon.TrayMouseMove += (s, e) =>
        {
            _lastSpeakerMouseMoveTick = Environment.TickCount64;
            SwiftVolumeNativeMethods.GetCursorPos(out var p);
            _lastSpeakerMouseX = p.X;
            _lastSpeakerMouseY = p.Y;
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
            var settings = _settingsAccessor();
            if (!settings.EnableTaskbarVolumeWheel) return;

            float delta = e.Delta > 0 ? 1.0f : -1.0f;
            float newVol = AudioDeviceHelper.StepVolume(delta);
            bool isMuted = AudioDeviceHelper.GetIsMuted();
            _onVolumeChanged?.Invoke(newVol, isMuted);
            UpdateIcons(newVol, isMuted, true);
        };
        try { _speakerIcon.ForceCreate(); } catch { }
    }

    private void InitMicIcon()
    {
        if (_micIcon != null)
        {
            UpdateMicVisibility();
            return;
        }

        var menu = new ContextMenu();
        EnableDismissOnOutsideClick(menu);

        _micIcon = new TaskbarIcon
        {
            Id = MicTrayIconGuid,
            ToolTipText = LocalizationService.Instance["SwiftVolume_Tray_MicTooltip"]
        };

        _micIcon.TrayLeftMouseUp += (s, e) =>
        {
            _mixerWindow?.ShowAtCursorOrTray();
        };

        _micIcon.TrayRightMouseUp += async (s, e) =>
        {
            await ShowContextMenuAsync(menu);
        };

        _micIcon.TrayMiddleMouseDown += (s, e) =>
        {
            bool newMuted = AudioDeviceHelper.ToggleAllInputMute();
            _lastMicMuted = newMuted;
            UpdateMicIcon(explicitMuted: newMuted, explicitActive: false, force: true);
            _onMicMuteChanged?.Invoke(newMuted);
        };

        UpdateMicVisibility();
        try
        {
            if (_settingsAccessor().ShowMicTrayIcon)
            {
                _micIcon.ForceCreate();
            }
        }
        catch { }
    }

    public void UpdateMicVisibility()
    {
        if (_micIcon == null) return;
        var settings = _settingsAccessor();
        bool show = settings.ShowMicTrayIcon;
        _micIcon.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            try { _micIcon.ForceCreate(); } catch { }
            UpdateMicIcon(force: true);
        }
    }

    public void UpdateSettings()
    {
        UpdateMicVisibility();
    }

    /// <summary>
    /// 指定された物理スクリーン座標 (x, y) が SwiftVolume のスピーカーアイコン上にあるかを判定します。
    /// （SanmiToys 本体のアイコンや通知領域の他のアイコン・時計は完全に除外）
    /// </summary>
    public bool IsCursorOnSpeakerIcon(int x, int y)
    {
        if (_speakerIcon == null || _speakerIcon.Visibility != Visibility.Visible) return false;

        try
        {
            // 1. Shell_NotifyIconGetRect による Explorer 上の正確なアイコン矩形判定 (Win7〜Win11公式API)
            var nid = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = _speakerIcon.TrayIcon?.WindowHandle ?? IntPtr.Zero,
                guidItem = SpeakerTrayIconGuid
            };

            int hr = Shell_NotifyIconGetRect(ref nid, out RECT rc);
            if (hr == 0) // S_OK
            {
                // アイコンの境界付近でも確実に反応するようマージン (±3px) を付与
                if (x >= rc.Left - 3 && x <= rc.Right + 3 &&
                    y >= rc.Top - 3 && y <= rc.Bottom + 3)
                {
                    return true;
                }
            }
        }
        catch { }

        // 2. 直近 (1200ms以内) に SV アイコン上で TrayMouseMove イベントを受信しており、
        //    かつその座標の近傍 (32px以内) にある場合のフォールバック判定
        if (Environment.TickCount64 - _lastSpeakerMouseMoveTick < 1200)
        {
            int dx = x - _lastSpeakerMouseX;
            int dy = y - _lastSpeakerMouseY;
            if ((dx * dx + dy * dy) <= 36 * 36)
            {
                return true;
            }
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void EnableDismissOnOutsideClick(ContextMenu menu)
    {
        menu.Opened += (s, e) =>
        {
            try
            {
                System.Windows.Input.Mouse.Capture(menu, System.Windows.Input.CaptureMode.SubTree);
            }
            catch { }
        };

        System.Windows.Input.Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(menu, (s, e) =>
        {
            menu.IsOpen = false;
            try
            {
                if (System.Windows.Input.Mouse.Captured == menu)
                {
                    System.Windows.Input.Mouse.Capture(null);
                }
            }
            catch { }
        });

        menu.Closed += (s, e) =>
        {
            try
            {
                if (System.Windows.Input.Mouse.Captured == menu)
                {
                    System.Windows.Input.Mouse.Capture(null);
                }
            }
            catch { }
        };
    }

    private async Task ShowContextMenuAsync(ContextMenu menu)
    {
        // フォーカスを自プロセスに奪還し、メニュー外クリックで確実に閉じられるようにする
        if (_mixerWindow != null)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_mixerWindow).Handle;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }

        // キャッシュが存在する場合は即座に 0ms でメニューを表示
        if (_cachedInputDevices.Count > 0 || _cachedOutputDevices.Count > 0)
        {
            PopulateFullContextMenu(menu, _cachedInputDevices, _cachedOutputDevices);
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;

            // バックグラウンドで最新情報を更新
            _ = RefreshCachedDevicesAsync();
            return;
        }

        // 初回キャッシュ未完了時のみ取得して表示
        await RefreshCachedDevicesAsync();
        PopulateFullContextMenu(menu, _cachedInputDevices, _cachedOutputDevices);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private async Task RefreshCachedDevicesAsync()
    {
        try
        {
            var inTask = Task.Run(() => _deviceService.GetSafeInputDevices());
            var outTask = Task.Run(() => _deviceService.GetSafeOutputDevices());
            await Task.WhenAll(inTask, outTask);
            _cachedInputDevices = inTask.Result;
            _cachedOutputDevices = outTask.Result;
        }
        catch { }
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
                        AudioDeviceHelper.PreApplyDeviceVolume(capturedId);
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

    public void UpdateMicIcon(bool? explicitMuted = null, bool? explicitActive = null, bool force = false)
    {
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            bool muted = explicitMuted ?? AudioDeviceHelper.GetIsInputMuted();
            bool active = explicitActive ?? false;
            Application.Current.Dispatcher.InvokeAsync(() => UpdateMicIcon(muted, active, force));
            return;
        }

        if (explicitMuted == null || explicitActive == null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    bool muted = AudioDeviceHelper.GetIsInputMuted();
                    Application.Current?.Dispatcher.InvokeAsync(() => UpdateMicIcon(muted, false, force));
                }
                catch { }
            });
            return;
        }

        try
        {
            bool isMuted = explicitMuted.Value;
            bool isActive = explicitActive.Value;

            if (force || _lastMicMuted != isMuted || _lastMicActive != isActive)
            {
                _lastMicMuted = isMuted;
                _lastMicActive = isActive;
                UpdateMicIconGraphic(isMuted, isActive);
            }
        }
        catch { }
    }


    private string _currentIconKey = "";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _pngBytesCache = new();

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
        vol = Math.Clamp(vol, 0f, 100f);

        int stage = 0;
        if (vol > 0 && !isMuted)
        {
            if (vol <= 33.3f) stage = 1;
            else if (vol <= 66.6f) stage = 2;
            else stage = 3;
        }

        string prefix = IsSystemDarkTheme() ? "spk_white" : "spk_dark";
        string cacheKey = (isMuted || stage == 0) ? $"{prefix}_0" : $"{prefix}_{stage}";

        _speakerIcon.ToolTipText = $"SwiftVolume - 音量: {(int)vol}%{(isMuted ? " (ミュート)" : "")}";

        // 同じアイコンキーかつアイコンが存在する場合は再描画をスキップ
        if (cacheKey == _currentIconKey && _speakerIcon.Icon != null)
        {
            return;
        }

        _currentIconKey = cacheKey;

        var icon = CreateFreshSpeakerIcon(cacheKey);
        if (icon != null)
        {
            _speakerIcon.Icon = icon;
            _speakerIcon.Visibility = Visibility.Visible;
            Debug.WriteLine($"[SV-ICON] Set Fresh Icon: {cacheKey} (vol={vol})");
        }
    }

    private static byte[]? GetSpeakerPngBytes(string cacheKey)
    {
        if (_pngBytesCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            string iconPath = $"/SanmiToys.Modules.SwiftVolume;component/Icons/{cacheKey}.png";
            var uri = new Uri($"pack://application:,,,{iconPath}", UriKind.Absolute);

            var resourceInfo = Application.GetResourceStream(uri);
            if (resourceInfo == null) return null;

            using var stream = resourceInfo.Stream;
            using var origBitmap = new System.Drawing.Bitmap(stream);

            bool isDarkTheme = cacheKey.StartsWith("spk_white", StringComparison.OrdinalIgnoreCase);
            bool isMute = cacheKey.EndsWith("_0", StringComparison.OrdinalIgnoreCase);

            // 1024x1024の元画像段階で非アクティブな暗色グレー波（R=67）を透明化
            // (リサイズ前に行うことでアンチエイリアスの破綻や波の欠落を完全防止)
            using var cleanOrig = (System.Drawing.Bitmap)origBitmap.Clone();
            if (!isMute)
            {
                var rect = new System.Drawing.Rectangle(0, 0, cleanOrig.Width, cleanOrig.Height);
                var data = cleanOrig.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                int totalBytes = data.Height * data.Stride;
                byte[] pixelBuffer = new byte[totalBytes];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixelBuffer, 0, totalBytes);

                for (int i = 0; i < totalBytes; i += 4)
                {
                    byte r = pixelBuffer[i + 2];
                    byte a = pixelBuffer[i + 3];
                    if (a > 20)
                    {
                        if (isDarkTheme && r < 120)
                        {
                            pixelBuffer[i + 3] = 0; // 完全透明化
                        }
                        else if (!isDarkTheme && r > 80)
                        {
                            pixelBuffer[i + 3] = 0; // 完全透明化
                        }
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(pixelBuffer, 0, data.Scan0, totalBytes);
                cleanOrig.UnlockBits(data);
            }

            using var resizedBitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(resizedBitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(cleanOrig, 0, 0, 32, 32);
            }

            using var ms = new MemoryStream();
            resizedBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            byte[] bytes = ms.ToArray();

            _pngBytesCache[cacheKey] = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Failed to load PNG {cacheKey}: {ex.Message}");
        }

        return null;
    }

    private static System.Drawing.Icon? CreateFreshSpeakerIcon(string cacheKey)
    {
        byte[]? bytes = GetSpeakerPngBytes(cacheKey);
        if (bytes == null || bytes.Length == 0) return null;
        return CreateIconFromPng(bytes, 32, 32);
    }

    private void UpdateMicIconGraphic(bool isMuted, bool isActive)
    {
        if (_micIcon == null || _micIcon.Visibility != Visibility.Visible) return;

        string theme = IsSystemDarkTheme() ? "white" : "dark";
        string state;
        if (isMuted) state = "off";
        else if (isActive) state = "active";
        else state = "on";

        string cacheKey = $"mic_{theme}_{state}";

        var loc = SanmiToys.Core.Services.LocalizationService.Instance;
        string statusText = isMuted ? " (ミュート)" : "";
        _micIcon.ToolTipText = $"{loc["SwiftVolume_Tray_MicTooltip"]}{statusText}";

        // 同じアイコンキーかつアイコンが存在する場合は再描画をスキップ
        if (cacheKey == _currentMicIconKey && _micIcon.Icon != null)
        {
            return;
        }

        _currentMicIconKey = cacheKey;

        var icon = CreateFreshMicIcon(cacheKey);
        if (icon != null)
        {
            _micIcon.Icon = icon;
            _micIcon.Visibility = Visibility.Visible;
            Debug.WriteLine($"[SV-MIC-ICON] Set Fresh Mic Icon: {cacheKey} (muted={isMuted}, active={isActive})");
        }
    }

    private static byte[]? GetMicPngBytes(string cacheKey)
    {
        if (_pngBytesCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
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

            using var ms = new MemoryStream();
            resizedBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            byte[] bytes = ms.ToArray();

            _pngBytesCache[cacheKey] = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            SanmiToys.Core.Services.AppLogger.Warn("SwiftVolume", $"Failed to load Mic PNG {cacheKey}: {ex.Message}");
        }

        return null;
    }

    private static System.Drawing.Icon? CreateFreshMicIcon(string cacheKey)
    {
        byte[]? bytes = GetMicPngBytes(cacheKey);
        if (bytes == null || bytes.Length == 0) return null;
        return CreateIconFromPng(bytes, 32, 32);
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
        _meteringService.Dispose();
        _deviceService.Dispose();
    }
}

