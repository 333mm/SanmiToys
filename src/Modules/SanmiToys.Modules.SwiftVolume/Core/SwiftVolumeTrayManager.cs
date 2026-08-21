using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
    private readonly Dictionary<string, System.Drawing.Icon> _iconCache = new();

    private float _lastSpeakerVol = -1f;
    private bool _lastSpeakerMuted = false;

    public SwiftVolumeTrayManager(Func<SwiftVolumeSettings> settingsAccessor, Action openSettingsAction, Action<float, bool>? onVolumeChanged = null, Action<string, bool>? onDeviceSwitched = null)
    {
        _settingsAccessor = settingsAccessor;
        _openSettingsAction = openSettingsAction;
        _onVolumeChanged = onVolumeChanged;
        _onDeviceSwitched = onDeviceSwitched;

        AudioDeviceHelper.MasterVolumeChanged += (vol, muted) =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons());
        };
        AudioDeviceHelper.DefaultDeviceChanged += () =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() => UpdateIcons());
        };

        // フェイルセーフ用の低頻度ポーリング (3秒間隔)
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.0) };
        _pollTimer.Tick += (s, e) => UpdateIcons();
    }

    public void Start()
    {
        Application.Current?.Dispatcher.Invoke(() =>
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
            UpdateIcons();
        });
    }

    public void Stop()
    {
        Application.Current?.Dispatcher.Invoke(() =>
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
        });
    }

    private void InitSpeakerIcon()
    {
        if (_speakerIcon != null)
        {
            _speakerIcon.Visibility = Visibility.Visible;
            return;
        }

        var menu = new ContextMenu();
        menu.Opened += (s, e) => PopulateSpeakerContextMenu(menu);

        _speakerIcon = new TaskbarIcon
        {
            ToolTipText = "SwiftVolume",
            ContextMenu = menu,
            Visibility = Visibility.Visible
        };
        _speakerIcon.TrayLeftMouseUp += (s, e) =>
        {
            _mixerWindow?.ShowAtCursorOrTray();
        };
        _speakerIcon.TrayMiddleMouseDown += (s, e) =>
        {
            var settings = _settingsAccessor();
            if (settings.MiddleClickMuteAll)
            {
                AudioDeviceHelper.ToggleMute();
                UpdateIcons();
            }
        };
        _speakerIcon.PreviewMouseWheel += (s, e) =>
        {
            float delta = e.Delta > 0 ? 1.0f : -1.0f;
            float newVol = AudioDeviceHelper.StepVolume(delta);
            bool isMuted = AudioDeviceHelper.GetIsMuted();
            _onVolumeChanged?.Invoke(newVol, isMuted);
            UpdateIcons();
        };
        try { _speakerIcon.ForceCreate(); } catch { }

        // 作成直後に即座にアイコンをセット
        try
        {
            float vol = AudioDeviceHelper.GetMasterVolume();
            bool isMuted = AudioDeviceHelper.GetIsMuted();
            _lastSpeakerVol = vol;
            _lastSpeakerMuted = isMuted;
            UpdateSpeakerIconGraphic(vol, isMuted);
        }
        catch { }
    }

    private void PopulateSpeakerContextMenu(ContextMenu menu)
    {
        PopulateFullContextMenu(menu);
    }

    private void PopulateMicContextMenu(ContextMenu menu)
    {
        PopulateFullContextMenu(menu);
    }

    private void PopulateFullContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        var loc = SanmiToys.Core.Services.LocalizationService.Instance;

        // 1. 既定のマイク 一覧
        try
        {
            var inputDevices = _deviceService.GetSafeInputDevices();
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
                        UpdateIcons();
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
            var outputDevices = _deviceService.GetSafeOutputDevices();
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
                        UpdateIcons();
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

    public void UpdateIcons()
    {
        try
        {
            float vol = AudioDeviceHelper.GetMasterVolume();
            bool isSpkMuted = AudioDeviceHelper.GetIsMuted();
            bool isMicMuted = isSpkMuted;

            if (Math.Abs(_lastSpeakerVol - vol) > 0.5f || _lastSpeakerMuted != isSpkMuted)
            {
                _lastSpeakerVol = vol;
                _lastSpeakerMuted = isSpkMuted;
                UpdateSpeakerIconGraphic(vol, isSpkMuted);
            }
        }
        catch { }
    }

    private void UpdateSpeakerIconGraphic(float volume, bool isMuted)
    {
        if (_speakerIcon == null) return;

        int stage = 0;
        if (volume > 0 && !isMuted)
        {
            if (volume <= 33) stage = 1;
            else if (volume <= 66) stage = 2;
            else stage = 3;
        }

        string cacheKey = isMuted ? "spk_white_0" : $"spk_white_{stage}";
        if (!_iconCache.TryGetValue(cacheKey, out var icon))
        {
            icon = LoadOriginalSpeakerIcon(cacheKey);
            if (icon != null) _iconCache[cacheKey] = icon;
        }

        if (icon != null)
        {
            _speakerIcon.Icon = icon;
            _speakerIcon.ToolTipText = $"SwiftVolume - 音量: {(int)volume}%{(isMuted ? " (ミュート)" : "")}";
        }
    }

    private static System.Drawing.Icon? LoadOriginalSpeakerIcon(string cacheKey)
    {
        try
        {
            string iconPath = $"/SanmiToys.Modules.SwiftVolume;component/Icons/{cacheKey}.png";
            var uri = new Uri($"pack://application:,,,{iconPath}", UriKind.Absolute);

            var resourceInfo = Application.GetResourceStream(uri);
            if (resourceInfo == null) return null;

            BitmapImage source;
            using (var stream = resourceInfo.Stream)
            {
                source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.StreamSource = stream;
                source.EndInit();
                source.Freeze();
            }

            int size = 32;
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();

            using (var context = visual.RenderOpen())
            {
                double scale = 1.0;
                double offset = (size - (size * scale)) / 2.0;

                context.PushTransform(new TranslateTransform(offset, offset));
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawImage(source, new Rect(0, 0, size, size));
                context.Pop();
                context.Pop();
            }

            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            rtb.Render(visual);
            rtb.Freeze();

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            encoder.Save(ms);
            ms.Position = 0;

            using var winBitmap = new Bitmap(ms);
            IntPtr hIcon = winBitmap.GetHicon();
            try
            {
                using var temp = System.Drawing.Icon.FromHandle(hIcon);
                return (System.Drawing.Icon)temp.Clone();
            }
            finally
            {
                SwiftVolumeNativeMethods.DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (var icon in _iconCache.Values)
        {
            icon.Dispose();
        }
        _iconCache.Clear();
    }
}
