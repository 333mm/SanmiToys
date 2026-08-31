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
    private long _restoringUntilTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(5).Ticks;
    private List<SafeDeviceInfo> _cachedInputDevices = new();
    private List<SafeDeviceInfo> _cachedOutputDevices = new();
    private System.IO.FileSystemWatcher? _fxSoundWatcher;

    public SwiftVolumeTrayManager(Func<SwiftVolumeSettings> settingsAccessor, Action openSettingsAction, Action<float, bool>? onVolumeChanged = null, Action<string, bool>? onDeviceSwitched = null)
    {
        _settingsAccessor = settingsAccessor;
        _openSettingsAction = openSettingsAction;
        _onVolumeChanged = onVolumeChanged;
        _onDeviceSwitched = onDeviceSwitched;

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
                _fxSoundWatcher.Changed += (s, e) =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(async () =>
                    {
                        // FxSound の出力先デバイス切り替えを待ってから連動先デバイスの音量を引き継ぐ
                        await Task.Delay(120);

                        string defName = AudioDeviceHelper.GetDefaultDeviceName();
                        if (defName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
                        {
                            var (newFxId, newFxName) = AudioDeviceHelper.GetFxSoundOutputDevice();
                            var settings = _settingsAccessor();
                            float targetVol = -1f;

                            // 1. 新しい連動先デバイスの保存音量または実音量を取得して引き継ぐ
                            if (!string.IsNullOrEmpty(newFxName) && settings.DeviceMasterVolumes.TryGetValue(newFxName, out float savedDevVol))
                            {
                                targetVol = savedDevVol * 100f;
                            }
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
                                string effKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(defName);
                                if (settings.DeviceMasterVolumes.TryGetValue(effKey, out float savedVol))
                                {
                                    targetVol = savedVol * 100f;
                                }
                            }

                            if (targetVol >= 0)
                            {
                                Interlocked.Exchange(ref _restoringUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(1200).Ticks);
                                AudioDeviceHelper.SetMasterVolume(targetVol);
                                string key = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(defName);
                                settings.DeviceMasterVolumes[key] = targetVol / 100f;
                                SwiftVolumeSettingsHelper.SaveSettingsDebounced(settings);
                                UpdateIcons(targetVol, false, true);
                            }
                        }

                        ApplyAllAppVolumesAsync();
                    });
                };
            }
        }
        catch { }
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
                RestoreAllDeviceVolumesAsync();
                ApplyAllAppVolumesAsync();
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
        EnableDismissOnOutsideClick(menu);

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

