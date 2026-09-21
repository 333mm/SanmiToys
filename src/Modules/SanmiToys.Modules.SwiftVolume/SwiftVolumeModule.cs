using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SanmiToys.Core;
using SanmiToys.Core.Helpers;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using SanmiToys.Modules.SwiftVolume.Core;
using SanmiToys.Modules.SwiftVolume.Models;
using SanmiToys.Modules.SwiftVolume.Views;

namespace SanmiToys.Modules.SwiftVolume;

public class SwiftVolumeModule : IToyModule
{
    private const int HOTKEY_ID_OPEN_MIXER = 0x5601;
    private const int HOTKEY_ID_MUTE = 0x5602;
    private const int HOTKEY_ID_MIC_MUTE = 0x5603;

    private readonly SettingsService _settingsService;
    private readonly Action<string>? _navigateSettingsAction;
    private SwiftVolumeSettings _settings = new();
    private GlobalVolumeWheelEngine? _wheelEngine;
    private SwiftVolumeTrayManager? _trayManager;
    private VolumeHudWindow? _hudWindow;
    private MixerWindow? _mixerWindow;
    private HwndSource? _hwndSource;

    public static SwiftVolumeModule? Instance { get; private set; }
    public MicMonitoringEngine? MonitorEngine { get; private set; }

    public string Id => "SwiftVolume";
    public string Name => "SwiftVolume";
    public string Description => LocalizationService.Instance["SwiftVolume_Desc"];
    public string IconGlyph => "\uE767"; // Speaker/Volume icon

    public bool IsEnabled
    {
        get => _settings.IsEnabled;
        set
        {
            if (_settings.IsEnabled != value)
            {
                _settings.IsEnabled = value;
                _settingsService.SetModuleSettings(Id, _settings);
                _settingsService.SetModuleEnabled(Id, value);
                try
                {
                    if (value) Start(); else Stop();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("SwiftVolume", $"Error toggling module IsEnabled ({value}): {ex.Message}");
                }
            }
        }
    }

    public SwiftVolumeModule(SettingsService settingsService, Action<string>? navigateSettingsAction = null)
    {
        Instance = this;
        _settingsService = settingsService;
        _navigateSettingsAction = navigateSettingsAction;
        _settings = _settingsService.GetModuleSettings<SwiftVolumeSettings>(Id);
        _settings.IsEnabled = _settingsService.IsModuleEnabled(Id, false);
        MonitorEngine = new MicMonitoringEngine(() => _settings);
    }

    private static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;
        if (app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.InvokeAsync(action);
        }
    }

    public Task InitializeAsync()
    {
        RunOnUi(() =>
        {
            _hudWindow = new VolumeHudWindow();
        });

        _trayManager = new SwiftVolumeTrayManager(
            () => _settings,
            () => _navigateSettingsAction?.Invoke(Id),
            OnVolumeChanged,
            OnDeviceChanged,
            OnMicMuteChanged,
            OnDeviceSwitching,
            () => GetOrCreateMixerWindow());
        _wheelEngine = new GlobalVolumeWheelEngine(() => _settings, OnVolumeChanged, pt => _trayManager?.IsCursorOnSpeakerIcon(pt.x, pt.y) ?? false);

        if (_settings.IsEnabled)
        {
            Start();
        }

        return Task.CompletedTask;
    }

    private void OnVolumeChanged(float newVolume, bool isMuted)
    {
        _trayManager?.UpdateIcons(newVolume, isMuted, true);
        if (_settings.ShowHud)
        {
            RunOnUi(() =>
            {
                _hudWindow?.ShowVolume(newVolume, isMuted, _settings.HudDurationSeconds, _settings.HudPosition, _settings.HudSize);
            });
        }
    }

    public void NotifyDeviceSwitching(string deviceName, bool isInput = false)
    {
        OnDeviceSwitching(deviceName, isInput);
    }

    private void OnDeviceSwitching(string deviceName, bool isInput)
    {
        if (_settings.ShowDeviceSwitchHud)
        {
            RunOnUi(() =>
            {
                _hudWindow?.ShowDeviceSwitching(deviceName, isInput, _settings.HudPosition, _settings.HudSize);
            });
        }
    }

    private void OnDeviceChanged(string deviceName, bool isInput)
    {
        if (_settings.EnableMicMonitoring)
        {
            MonitorEngine?.Restart();
        }

        if (_settings.ShowDeviceSwitchHud)
        {
            RunOnUi(() =>
            {
                _hudWindow?.ShowDeviceSwitch(deviceName, isInput, _settings.HudDurationSeconds, _settings.HudPosition, _settings.HudSize);
            });
        }
    }

    public void NotifyMicMuteChanged(bool isMuted)
    {
        OnMicMuteChanged(isMuted);
    }

    private void OnMicMuteChanged(bool isMuted)
    {
        if (_settings.PlayMicMuteSound)
        {
            MicSoundPlayer.PlayMuteStateSound(isMuted);
        }

        if (_settings.ShowHud)
        {
            RunOnUi(() =>
            {
                _hudWindow?.ShowMicMute(isMuted, _settings.HudDurationSeconds, _settings.HudPosition, _settings.HudSize);
            });
        }
    }

    public MixerWindow GetOrCreateMixerWindow()
    {
        if (_mixerWindow == null)
        {
            _mixerWindow = new MixerWindow(() => _settings);
        }
        return _mixerWindow;
    }

    public void TriggerOpenMixer()
    {
        RunOnUi(() =>
        {
            GetOrCreateMixerWindow().ShowAtCursorOrTray();
        });
    }

    public void Start()
    {
        _wheelEngine?.Start();
        _trayManager?.Start();
        EnsureMessageWindow();
        UpdateHotkeyRegistrations();

        if (_settings.EnableMicMonitoring)
        {
            MonitorEngine?.Start();
        }
    }

    public void Stop()
    {
        try { MonitorEngine?.Stop(); } catch { }
        try { _wheelEngine?.Stop(); } catch { }
        try { _trayManager?.Stop(); } catch { }
        try { _mixerWindow?.Hide(); } catch { }
        try { UnregisterHotkeys(); } catch { }
        if (_hwndSource != null)
        {
            try { _hwndSource.Dispose(); } catch { }
            _hwndSource = null;
        }
        try { SwiftVolume.Helpers.SwiftVolumeSettingsHelper.SaveSettingsImmediately(_settings); } catch { }
    }

    private void EnsureMessageWindow()
    {
        if (_hwndSource == null)
        {
            RunOnUi(() =>
            {
                var parameters = new HwndSourceParameters("SwiftVolumeHotkeyMessageSink")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0x800000 // WS_BORDER invisible
                };
                _hwndSource = new HwndSource(parameters);
                _hwndSource.AddHook(HwndHook);
            });
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int hotkeyId = wParam.ToInt32();
            switch (hotkeyId)
            {
                case HOTKEY_ID_OPEN_MIXER:
                    TriggerOpenMixer();
                    handled = true;
                    break;
                case HOTKEY_ID_MUTE:
                    var (vol, muted) = AudioDeviceHelper.ToggleMute();
                    _trayManager?.UpdateIconsExplicit(vol, muted);
                    OnVolumeChanged(vol, muted);
                    handled = true;
                    break;
                case HOTKEY_ID_MIC_MUTE:
                    bool micMuted = AudioDeviceHelper.ToggleAllInputMute();
                    _trayManager?.UpdateMicIcon(explicitMuted: micMuted, force: true);
                    OnMicMuteChanged(micMuted);
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    public void UpdateHotkeyRegistrations()
    {
        EnsureMessageWindow();
        if (_hwndSource == null) return;

        UnregisterHotkeys();

        if (!IsEnabled) return;

        RegisterSingleHotkey(HOTKEY_ID_OPEN_MIXER, _settings.HotkeyOpenMixerEnabled, _settings.HotkeyOpenMixerCtrl, _settings.HotkeyOpenMixerAlt, _settings.HotkeyOpenMixerShift, _settings.HotkeyOpenMixerWin, _settings.HotkeyOpenMixer);
        RegisterSingleHotkey(HOTKEY_ID_MUTE, _settings.HotkeyMuteEnabled, _settings.HotkeyMuteCtrl, _settings.HotkeyMuteAlt, _settings.HotkeyMuteShift, _settings.HotkeyMuteWin, _settings.HotkeyMute);
        RegisterSingleHotkey(HOTKEY_ID_MIC_MUTE, _settings.HotkeyMicMuteEnabled, _settings.HotkeyMicMuteCtrl, _settings.HotkeyMicMuteAlt, _settings.HotkeyMicMuteShift, _settings.HotkeyMicMuteWin, _settings.HotkeyMicMute);
    }

    private void RegisterSingleHotkey(int id, bool enabled, bool ctrl, bool alt, bool shift, bool win, string keyName)
    {
        if (!enabled || string.IsNullOrEmpty(keyName) || keyName == "None" || _hwndSource == null) return;

        uint modifiers = NativeMethods.MOD_NOREPEAT;
        if (ctrl) modifiers |= NativeMethods.MOD_CONTROL;
        if (alt) modifiers |= NativeMethods.MOD_ALT;
        if (shift) modifiers |= NativeMethods.MOD_SHIFT;
        if (win) modifiers |= NativeMethods.MOD_WIN;

        if (Enum.TryParse<Key>(keyName, true, out var key) && key != Key.None)
        {
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk != 0)
            {
                NativeMethods.RegisterHotKey(_hwndSource.Handle, id, modifiers, vk);
            }
        }
    }

    private void UnregisterHotkeys()
    {
        if (_hwndSource != null)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_OPEN_MIXER);
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_MUTE);
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_MIC_MUTE);
        }
    }

    public void NotifySettingsChanged()
    {
        _trayManager?.UpdateSettings();
    }

    public void OpenSettings()
    {
        RunOnUi(() =>
        {
            _navigateSettingsAction?.Invoke(Id);
        });
    }

    public object? CreateSettingsView()
    {
        return new SwiftVolumeSettingsView(this, _settingsService, _settings);
    }
}
