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
                if (value) Start(); else Stop();
            }
        }
    }

    public SwiftVolumeModule(SettingsService settingsService, Action<string>? navigateSettingsAction = null)
    {
        _settingsService = settingsService;
        _navigateSettingsAction = navigateSettingsAction;
        _settings = _settingsService.GetModuleSettings<SwiftVolumeSettings>(Id);
        _settings.IsEnabled = _settingsService.IsModuleEnabled(Id, false);
    }

    public Task InitializeAsync()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _hudWindow = new VolumeHudWindow();
        });

        _wheelEngine = new GlobalVolumeWheelEngine(() => _settings, OnVolumeChanged);
        _trayManager = new SwiftVolumeTrayManager(() => _settings, () => _navigateSettingsAction?.Invoke(Id), OnVolumeChanged, OnDeviceChanged);

        if (_settings.IsEnabled)
        {
            Start();
        }

        return Task.CompletedTask;
    }

    private void OnVolumeChanged(float newVolume, bool isMuted)
    {
        _trayManager?.UpdateIcons();

        if (_settings.ShowHud)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _hudWindow?.ShowVolume(newVolume, isMuted, _settings.HudDurationSeconds, _settings.HudPosition, _settings.HudSize);
            });
        }
    }

    private void OnDeviceChanged(string deviceName, bool isInput)
    {
        if (_settings.ShowDeviceSwitchHud)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _hudWindow?.ShowDeviceSwitch(deviceName, isInput, _settings.HudDurationSeconds, _settings.HudPosition, _settings.HudSize);
            });
        }
    }

    public void TriggerOpenMixer()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_mixerWindow == null)
            {
                _mixerWindow = new MixerWindow(() => _settings);
            }
            _mixerWindow.ShowAtCursorOrTray();
        });
    }

    public void Start()
    {
        _wheelEngine?.Start();
        _trayManager?.Start();
        EnsureMessageWindow();
        UpdateHotkeyRegistrations();
    }

    public void Stop()
    {
        _wheelEngine?.Stop();
        _trayManager?.Stop();
        _mixerWindow?.Hide();
        UnregisterHotkeys();
        if (_hwndSource != null)
        {
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    private void EnsureMessageWindow()
    {
        if (_hwndSource == null)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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
                    bool muted = AudioDeviceHelper.ToggleMute();
                    OnVolumeChanged(AudioDeviceHelper.GetMasterVolume(), muted);
                    handled = true;
                    break;
                case HOTKEY_ID_MIC_MUTE:
                    AudioDeviceHelper.ToggleInputMute();
                    _trayManager?.UpdateIcons();
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

    public object? CreateSettingsView()
    {
        return new SwiftVolumeSettingsView(this, _settingsService, _settings);
    }
}
