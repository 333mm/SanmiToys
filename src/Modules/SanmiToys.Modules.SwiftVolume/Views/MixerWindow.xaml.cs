using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using SanmiToys.Modules.SwiftVolume.Core;
using SanmiToys.Modules.SwiftVolume.Helpers;
using SanmiToys.Modules.SwiftVolume.Models;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SanmiToys.Modules.SwiftVolume.Views;

public partial class MixerWindow : Window
{
    public static readonly DependencyProperty OutputPeakLevelProperty =
        DependencyProperty.Register(nameof(OutputPeakLevel), typeof(float), typeof(MixerWindow), new PropertyMetadata(0f));

    public static readonly DependencyProperty InputPeakLevelProperty =
        DependencyProperty.Register(nameof(InputPeakLevel), typeof(float), typeof(MixerWindow), new PropertyMetadata(0f));

    public float OutputPeakLevel
    {
        get => (float)GetValue(OutputPeakLevelProperty);
        set => SetValue(OutputPeakLevelProperty, value);
    }

    public float InputPeakLevel
    {
        get => (float)GetValue(InputPeakLevelProperty);
        set => SetValue(InputPeakLevelProperty, value);
    }

    private readonly Func<SwiftVolumeSettings> _settingsAccessor;
    private readonly DeviceEnumerationService _deviceService = new();
    private readonly MeteringService _meteringService = new();
    private readonly DispatcherTimer _meterTimer;
    private DispatcherTimer? _focusMonitorTimer;
    private DateTime _lastShowTime = DateTime.MinValue;

    private float _smoothedOutputPeak = 0f;
    private float _smoothedInputPeak = 0f;

    private bool _isUpdatingUi = false;
    private bool _isExpanded = false;
    private List<SafeDeviceInfo> _outputDevices = new();
    private List<SafeDeviceInfo> _inputDevices = new();
    private SafeDeviceInfo? _currentOutputDevice;
    private SafeDeviceInfo? _currentInputDevice;
    private List<SafeAudioSession> _cachedSessions = new();
    private readonly Dictionary<string, Slider> _expandedDeviceSliders = new();
    private string? _singleLinkedDeviceId;
    private bool _isSelfDraggingMaster = false;
    private float _targetMasterVol = -1f;
    private int _masterVolWorkerRunning = 0;
    private float _targetInputVol = -1f;
    private int _inputVolWorkerRunning = 0;
    private System.IO.FileSystemWatcher? _fxSoundWatcher;

    private class SessionMeterItem
    {
        public SafeAudioSession Session { get; set; } = null!;
        public Border MeterBar { get; set; } = null!;
        public Grid Container { get; set; } = null!;
        public float SmoothedPeak { get; set; }
    }

    private readonly List<SessionMeterItem> _sessionMeters = new();

    public MixerWindow(Func<SwiftVolumeSettings> settingsAccessor)
    {
        InitializeComponent();
        _settingsAccessor = settingsAccessor;

        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

        new WindowInteropHelper(this).EnsureHandle();

        // 起動時にバックグラウンドでデバイスとセッション情報を事前キャッシュ（初回表示 0ms 化）
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var outs = _deviceService.GetSafeOutputDevices();
                var ins = _deviceService.GetSafeInputDevices();
                var def = outs.FirstOrDefault(d => d.IsDefault) ?? outs.FirstOrDefault();
                if (def != null)
                {
                    var sess = _deviceService.GetSafeSessions(def.Id);
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_outputDevices.Count == 0) _outputDevices = outs;
                        if (_inputDevices.Count == 0) _inputDevices = ins;
                        if (_currentOutputDevice == null) _currentOutputDevice = def;
                        if (_currentInputDevice == null) _currentInputDevice = ins.FirstOrDefault(d => d.IsDefault) ?? ins.FirstOrDefault();
                        if (_cachedSessions.Count == 0) _cachedSessions = sess;
                    });
                }
            }
            catch { }
        });

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (s, e) =>
        {
            if (!this.IsVisible) return;
            try
            {
                if (_currentOutputDevice != null)
                {
                    float raw = _meteringService.GetPeakLevel(_currentOutputDevice.Id);
                    raw = Math.Min(1.0f, raw * 1.5f);
                    if (raw > _smoothedOutputPeak)
                    {
                        // アタック（自然で滑らかな立ち上がり）
                        _smoothedOutputPeak = _smoothedOutputPeak + (raw - _smoothedOutputPeak) * 0.50f;
                    }
                    else
                    {
                        // リリース（美しく余韻を残して滑らかにフェードアウト）
                        _smoothedOutputPeak = Math.Max(0f, _smoothedOutputPeak * 0.94f - 0.0015f);
                    }
                    OutputPeakLevel = _smoothedOutputPeak;
                }
                if (_currentInputDevice != null)
                {
                    float raw = _meteringService.GetPeakLevel(_currentInputDevice.Id);
                    raw = Math.Min(1.0f, raw * 1.5f);
                    if (raw > _smoothedInputPeak)
                    {
                        _smoothedInputPeak = _smoothedInputPeak + (raw - _smoothedInputPeak) * 0.50f;
                    }
                    else
                    {
                        _smoothedInputPeak = Math.Max(0f, _smoothedInputPeak * 0.94f - 0.0015f);
                    }
                    InputPeakLevel = _smoothedInputPeak;
                }

                // 各アプリセッションのピークメーター更新
                for (int i = 0; i < _sessionMeters.Count; i++)
                {
                    var item = _sessionMeters[i];
                    float raw = 0f;
                    var targetControls = item.Session.Controls.Count > 0 ? item.Session.Controls : (item.Session.Control != null ? new List<AudioSessionControl> { item.Session.Control } : null);
                    if (targetControls != null)
                    {
                        foreach (var ctrl in targetControls)
                        {
                            try
                            {
                                if (ctrl.AudioMeterInformation != null)
                                {
                                    float v = ctrl.AudioMeterInformation.MasterPeakValue;
                                    if (v > raw) raw = v;
                                }
                            }
                            catch { }
                        }
                    }

                    // 感度を従来の半分程度（3.0 -> 1.5）に調整して過敏な振り切れを防止
                    raw = Math.Min(1.0f, raw * 1.5f);
                    if (raw > item.SmoothedPeak)
                    {
                        item.SmoothedPeak = item.SmoothedPeak + (raw - item.SmoothedPeak) * 0.50f;
                    }
                    else
                    {
                        // 下降時に心地よい余韻（滑らかなフォールオフ）を付加
                        item.SmoothedPeak = Math.Max(0f, item.SmoothedPeak * 0.94f - 0.0015f);
                    }

                    double maxW = Math.Max(0, item.Container.ActualWidth - 42);
                    item.MeterBar.Width = maxW * item.SmoothedPeak;
                }
            }
            catch { }
        };

        this.Deactivated += (s, e) =>
        {
            if ((DateTime.Now - _lastShowTime).TotalMilliseconds > 400)
            {
                CloseWindowSafely();
            }
        };

        this.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                CloseWindowSafely();
            }
        };

        this.SizeChanged += (s, e) =>
        {
            if (this.IsVisible && this.Opacity > 0.5)
            {
                UpdateWindowPosition();
            }
        };

        AudioDeviceHelper.DefaultDeviceChanged += () =>
        {
            Dispatcher.InvokeAsync(() => RefreshDataAsync());
        };

        MasterVolumeSlider.PreviewMouseDown += (s, e) => _isSelfDraggingMaster = true;
        MasterVolumeSlider.PreviewMouseUp += (s, e) => _isSelfDraggingMaster = false;
        MasterVolumeSlider.MouseLeave += (s, e) => { if (!MasterVolumeSlider.IsMouseCaptured) _isSelfDraggingMaster = false; };

        AudioDeviceHelper.MasterVolumeChanged += (vol, muted) =>
        {
            if (_isSelfDraggingMaster || MasterVolumeSlider.IsMouseCaptureWithin) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (_currentOutputDevice != null && _currentOutputDevice.IsDefault)
                {
                    _currentOutputDevice.Volume = vol / 100f;
                    _currentOutputDevice.IsMuted = muted;
                    UpdateMasterControls();

                    SyncLinkedSliders(vol);
                }
            });
        };

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
                    Dispatcher.InvokeAsync(async () =>
                    {
                        // FxSound側の出力デバイス切り替えが発生した場合、旧デバイスの連動を即時切断
                        _singleLinkedDeviceId = null;
                        await Task.Delay(120);

                        if (_currentOutputDevice != null && _currentOutputDevice.Name.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
                        {
                            var (newFxId, newFxName) = AudioDeviceHelper.GetFxSoundOutputDevice();
                            var settings = _settingsAccessor();
                            float targetVol = -1f;

                            // 1. 新しい連動先デバイスに設定されていた音量（保存音量または実音量）を取得して引き継ぐ
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

                            // 連動先デバイス自体の音量が未保存なら、FxSound+新デバイスの実効キー保存値を照合
                            if (targetVol < 0)
                            {
                                string effKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(_currentOutputDevice.Name);
                                if (settings.DeviceMasterVolumes.TryGetValue(effKey, out float savedVol))
                                {
                                    targetVol = savedVol * 100f;
                                }
                            }

                            // いずれもなければ現在の音量をフォールバック
                            if (targetVol < 0)
                            {
                                targetVol = AudioDeviceHelper.GetMasterVolume();
                            }

                            // 2. FxSound と UI スライダーに連動先デバイスの音量を反映
                            AudioDeviceHelper.SetMasterVolume(targetVol);
                            MasterVolumeSlider.Value = targetVol;

                            // 3. FxSound（新裏デバイス）の実効キーにも即座に保存
                            string key = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(_currentOutputDevice.Name);
                            settings.DeviceMasterVolumes[key] = targetVol / 100f;
                            SwiftVolumeSettingsHelper.SaveSettingsDebounced(settings);

                            // 4. 新しい連動先デバイスのみ連動スライダーを追従
                            _singleLinkedDeviceId = newFxId;
                            SyncLinkedSliders(targetVol);

                            // 5. アプリセッションも新裏デバイス用設定で再描画
                            RefreshDataAsync();
                        }
                    });
                };
            }
        }
        catch { }
    }

    private static (string? id, string? name) GetFxSoundOutputDevice()
    {
        try
        {
            string settingsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FxSound", "FxSound.settings");
            if (System.IO.File.Exists(settingsPath))
            {
                using var fs = new System.IO.FileStream(settingsPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                using var reader = new System.IO.StreamReader(fs);
                string xml = reader.ReadToEnd();

                string? devId = null;
                string? devName = null;

                var idMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<VALUE\s+name=""output_device_id""\s+val=""([^""]+)""");
                if (idMatch.Success && !string.IsNullOrWhiteSpace(idMatch.Groups[1].Value))
                {
                    devId = idMatch.Groups[1].Value.Trim();
                }

                var nameMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<VALUE\s+name=""output_device_name""\s+val=""([^""]+)""");
                if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                {
                    devName = System.Net.WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());
                }

                return (devId, devName);
            }
        }
        catch { }
        return (null, null);
    }

    private bool IsDeviceLinkedWithDefault(SafeDeviceInfo dev)
    {
        if (_currentOutputDevice == null) return false;

        // 1. 同一デバイスまたは既定デバイス
        if (dev.Id == _currentOutputDevice.Id || dev.IsDefault || dev.Name == _currentOutputDevice.Name)
        {
            return true;
        }

        // 2. 既定デバイスが FxSound の場合: FxSound が現在掴んでいる出力先裏デバイス「のみ」厳格に連動
        if (_currentOutputDevice.Name.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
        {
            var (fxId, fxName) = GetFxSoundOutputDevice();
            if (!string.IsNullOrEmpty(fxId) || !string.IsNullOrEmpty(fxName))
            {
                bool matchesId = !string.IsNullOrEmpty(fxId) && 
                                 (dev.Id.Equals(fxId, StringComparison.OrdinalIgnoreCase) || 
                                  dev.Id.Contains(fxId, StringComparison.OrdinalIgnoreCase) || 
                                  fxId.Contains(dev.Id, StringComparison.OrdinalIgnoreCase));

                bool matchesName = !string.IsNullOrEmpty(fxName) && 
                                   (dev.Name.Equals(fxName, StringComparison.OrdinalIgnoreCase) ||
                                    dev.Name.StartsWith(fxName, StringComparison.OrdinalIgnoreCase) ||
                                    fxName.StartsWith(dev.Name, StringComparison.OrdinalIgnoreCase));

                if (matchesId || matchesName)
                {
                    return true;
                }

                // FxSound の設定情報が存在する場合、現在の出力先デバイス以外（変更前の古いデバイスを含む）は 100% 連動させない
                return false;
            }

            // FxSound 設定ファイルが読めなかった場合のフォールバック
            if (!string.IsNullOrEmpty(_singleLinkedDeviceId) && dev.Id == _singleLinkedDeviceId)
            {
                return true;
            }

            return false;
        }

        // 3. FxSound が裏デバイスで既定が通常デバイスの場合:
        if (dev.Name.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
        {
            var (fxId, fxName) = GetFxSoundOutputDevice();
            if (!string.IsNullOrEmpty(fxId) || !string.IsNullOrEmpty(fxName))
            {
                bool matchesId = !string.IsNullOrEmpty(fxId) && 
                                 (_currentOutputDevice.Id.Equals(fxId, StringComparison.OrdinalIgnoreCase) || 
                                  _currentOutputDevice.Id.Contains(fxId, StringComparison.OrdinalIgnoreCase) || 
                                  fxId.Contains(_currentOutputDevice.Id, StringComparison.OrdinalIgnoreCase));

                bool matchesName = !string.IsNullOrEmpty(fxName) && 
                                   (_currentOutputDevice.Name.Equals(fxName, StringComparison.OrdinalIgnoreCase) ||
                                    _currentOutputDevice.Name.StartsWith(fxName, StringComparison.OrdinalIgnoreCase) ||
                                    fxName.StartsWith(_currentOutputDevice.Name, StringComparison.OrdinalIgnoreCase));

                return matchesId || matchesName;
            }
        }

        // 4. 動的連動が確認された特定デバイス1つのみ
        if (!string.IsNullOrEmpty(_singleLinkedDeviceId) && dev.Id == _singleLinkedDeviceId)
        {
            return true;
        }

        return false;
    }

    private void SyncLinkedSliders(float vol)
    {
        _isUpdatingUi = true;
        try
        {
            foreach (var dev in _outputDevices)
            {
                // 本当に連動しているデバイスのみ双方向連動（他デバイスは完全独立）
                if (IsDeviceLinkedWithDefault(dev))
                {
                    if (_expandedDeviceSliders.TryGetValue(dev.Id, out var expSlider))
                    {
                        if (!expSlider.IsMouseCaptureWithin && !expSlider.IsFocused)
                        {
                            expSlider.Value = vol;
                            dev.Volume = vol / 100f;
                        }
                    }
                }
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void QueueMasterVolumeUpdate(float vol)
    {
        _targetMasterVol = vol;
        if (Interlocked.Exchange(ref _masterVolWorkerRunning, 1) == 0)
        {
            Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        float v = _targetMasterVol;
                        AudioDeviceHelper.SetMasterVolume(v);

                        // 本当に連動している裏デバイスのみに反映（他デバイスは独立維持）
                        foreach (var dev in _outputDevices)
                        {
                            if (dev.Id != _currentOutputDevice?.Id && IsDeviceLinkedWithDefault(dev))
                            {
                                _deviceService.SetDeviceVolumeDirect(dev.Id, v);
                            }
                        }

                        await Task.Delay(25);
                        if (Math.Abs(_targetMasterVol - v) < 0.05f)
                        {
                            break;
                        }
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _masterVolWorkerRunning, 0);
                    if (_targetMasterVol >= 0 && Math.Abs(AudioDeviceHelper.GetMasterVolume() - _targetMasterVol) > 0.5f)
                    {
                        AudioDeviceHelper.SetMasterVolume(_targetMasterVol);
                    }
                }
            });
        }
    }

    private void QueueInputVolumeUpdate(float vol, string? devId)
    {
        _targetInputVol = vol;
        if (Interlocked.Exchange(ref _inputVolWorkerRunning, 1) == 0)
        {
            Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        float v = _targetInputVol;
                        AudioDeviceHelper.SetInputVolume(v, devId);

                        await Task.Delay(25);
                        if (Math.Abs(_targetInputVol - v) < 0.05f)
                        {
                            break;
                        }
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _inputVolWorkerRunning, 0);
                    if (_targetInputVol >= 0 && Math.Abs(AudioDeviceHelper.GetInputVolume() - _targetInputVol) > 0.5f)
                    {
                        AudioDeviceHelper.SetInputVolume(_targetInputVol, devId);
                    }
                }
            });
        }
    }

    public void ShowAtCursorOrTray()
    {
        _lastShowTime = DateTime.Now;

        // デバイスが未解決の場合は即座に既定デバイスを割り当て（初回表示時の未登録初期音量判定ミスを根絶）
        if (_currentOutputDevice == null)
        {
            string defName = AudioDeviceHelper.GetDefaultDeviceName();
            _currentOutputDevice = _outputDevices.FirstOrDefault(d => d.IsDefault || (!string.IsNullOrEmpty(defName) && d.Name == defName))
                                   ?? _outputDevices.FirstOrDefault();
            if (_currentOutputDevice == null && !string.IsNullOrEmpty(defName))
            {
                _currentOutputDevice = new SafeDeviceInfo { Name = defName, IsDefault = true };
            }
        }

        // 実際のマスター音量を即座に取得してスライダー・テキストを先行同期（表示ズレを完全解消）
        try
        {
            float curVol = AudioDeviceHelper.GetMasterVolume();
            bool curMuted = AudioDeviceHelper.GetIsMuted();
            if (_currentOutputDevice != null && _currentOutputDevice.IsDefault)
            {
                _currentOutputDevice.Volume = curVol / 100f;
                _currentOutputDevice.IsMuted = curMuted;
            }
            _isUpdatingUi = true;
            try
            {
                int vInt = (int)Math.Round(curVol);
                MasterVolumeSlider.Value = vInt;
                bool isMuted = curMuted || vInt == 0;
                MasterMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.SpeakerOff24 : SymbolRegular.Speaker224);
                MasterMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isMuted ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
        catch { }

        // 縦解像度に応じた最大サイズ設定
        double workAreaH = SystemParameters.WorkArea.Height;
        AppSessionsScrollViewer.MaxHeight = Math.Max(160, workAreaH - 220);
        this.MaxHeight = Math.Max(260, workAreaH - 20);

        double otherDevsCount = Math.Max(0, _outputDevices.Count - 1);
        double targetW = _isExpanded ? Math.Min(370 + (otherDevsCount * 280), SystemParameters.WorkArea.Width - 24) : 370;
        this.Width = targetW;

        // すでに計測済みのサイズをもとに直ちに位置を適用して 0ms で表示
        UpdateWindowPosition();
        this.Opacity = 1;
        this.Show();
        this.Activate();
        this.Focus();

        StartFocusMonitor();
        _meterTimer.Start();

        var hwnd = new WindowInteropHelper(this).Handle;
        WindowPlacementHelper.ForceForeground(hwnd);

        // キャッシュされたセッションがあれば即座に初期描画（体感 0ms 表示）
        if (_cachedSessions.Count > 0 && AppSessionsPanel.Children.Count == 0)
        {
            RenderAppSessions(_cachedSessions);
        }

        // バックグラウンドで非同期にデバイス＆セッション情報を高速並列更新
        RefreshDataAsync();
    }

    private void UpdateWindowPosition()
    {
        SwiftVolumeNativeMethods.GetCursorPos(out var p);

        double dpiScaleX = 1.0;
        double dpiScaleY = 1.0;

        SwiftVolumeNativeMethods.RECT rcMonitor = new()
        {
            Left = 0, Top = 0,
            Right = (int)SystemParameters.PrimaryScreenWidth,
            Bottom = (int)SystemParameters.PrimaryScreenHeight
        };
        SwiftVolumeNativeMethods.RECT rcWork = new()
        {
            Left = 0, Top = 0,
            Right = (int)SystemParameters.WorkArea.Width,
            Bottom = (int)SystemParameters.WorkArea.Height
        };

        try
        {
            IntPtr hMonitor = SwiftVolumeNativeMethods.MonitorFromPoint(p, SwiftVolumeNativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                if (SwiftVolumeNativeMethods.GetDpiForMonitor(hMonitor, SwiftVolumeNativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
                {
                    dpiScaleX = dpiX / 96.0;
                    dpiScaleY = dpiY / 96.0;
                }

                var mi = new SwiftVolumeNativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<SwiftVolumeNativeMethods.MONITORINFO>() };
                if (SwiftVolumeNativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    rcMonitor = mi.rcMonitor;
                    rcWork = mi.rcWork;
                }
            }
        }
        catch { }

        double workLeft = rcWork.Left / dpiScaleX;
        double workTop = rcWork.Top / dpiScaleY;
        double workRight = rcWork.Right / dpiScaleX;
        double workBottom = rcWork.Bottom / dpiScaleY;

        double actualW = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
        double actualH = this.ActualHeight > 0 ? this.ActualHeight : 280;

        // タスクバーの位置判定
        bool tbTop = rcWork.Top > rcMonitor.Top;
        bool tbLeft = rcWork.Left > rcMonitor.Left;
        bool tbRight = rcWork.Right < rcMonitor.Right;

        double cursorX = p.X / dpiScaleX;
        double cursorY = p.Y / dpiScaleY;

        double margin = 10.0;
        double finalLeft, finalTop;

        if (tbTop)
        {
            // 上部タスクバー: 下に伸ばす
            finalTop = workTop + margin;
            finalLeft = cursorX - (actualW / 2);
        }
        else if (tbLeft)
        {
            // 左側タスクバー
            finalLeft = workLeft + margin;
            finalTop = Math.Min(cursorY - (actualH / 2), workBottom - actualH - margin);
        }
        else if (tbRight)
        {
            // 右側タスクバー
            finalLeft = workRight - actualW - margin;
            finalTop = Math.Min(cursorY - (actualH / 2), workBottom - actualH - margin);
        }
        else
        {
            // 下部タスクバー（標準）: タスクバーを起点に上に伸ばす！
            finalTop = workBottom - actualH - margin;
            finalLeft = cursorX - (actualW / 2);
        }

        // ワークエリア内に確実にクランプ
        if (finalLeft < workLeft + 8) finalLeft = workLeft + 8;
        if (finalLeft + actualW > workRight - 8) finalLeft = workRight - actualW - 8;
        if (finalTop < workTop + 8) finalTop = workTop + 8;
        if (finalTop + actualH > workBottom - 8) finalTop = workBottom - actualH - 8;

        this.Left = finalLeft;
        this.Top = finalTop;
    }

    private void StartFocusMonitor()
    {
        _focusMonitorTimer?.Stop();
        _focusMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _focusMonitorTimer.Tick += (s, e) =>
        {
            if (!this.IsVisible)
            {
                _focusMonitorTimer.Stop();
                return;
            }

            if ((DateTime.Now - _lastShowTime).TotalMilliseconds < 600) return;

            var activeHwnd = SwiftVolumeNativeMethods.GetForegroundWindow();
            var myHwnd = new WindowInteropHelper(this).Handle;

            if (activeHwnd != myHwnd)
            {
                uint activeThread = SwiftVolumeNativeMethods.GetWindowThreadProcessId(activeHwnd, out uint activePid);
                uint myPid = (uint)Process.GetCurrentProcess().Id;
                if (activePid != myPid)
                {
                    CloseWindowSafely();
                }
            }
        };
        _focusMonitorTimer.Start();
    }

    private void StopFocusMonitor()
    {
        if (_focusMonitorTimer != null)
        {
            _focusMonitorTimer.Stop();
            _focusMonitorTimer = null;
        }
    }

    private void CloseWindowSafely()
    {
        StopFocusMonitor();
        _meterTimer.Stop();
        this.Hide();
        SwiftVolumeSettingsHelper.SaveSettingsImmediately(_settingsAccessor());
    }

    public void RefreshData()
    {
        RefreshDataAsync();
    }

    public async void RefreshDataAsync()
    {
        _isUpdatingUi = true;
        try
        {
            // 並列で入出力デバイスを取得
            var outDevicesTask = System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeOutputDevices());
            var inDevicesTask = System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeInputDevices());

            await System.Threading.Tasks.Task.WhenAll(outDevicesTask, inDevicesTask);
            var outDevices = outDevicesTask.Result;
            var inDevices = inDevicesTask.Result;

            _outputDevices = outDevices;
            _inputDevices = inDevices;

            var settings = _settingsAccessor();
            OutputDeviceCombo.Items.Clear();
            int selOutIdx = 0;
            for (int i = 0; i < _outputDevices.Count; i++)
            {
                var d = _outputDevices[i];
                string devKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(d.Name);
                if (settings.DeviceMasterVolumes.TryGetValue(devKey, out float savedVol) ||
                    settings.DeviceMasterVolumes.TryGetValue(d.Name, out savedVol))
                {
                    if (Math.Abs(d.Volume - savedVol) > 0.01f)
                    {
                        d.Volume = savedVol;
                        _deviceService.SetDeviceVolumeDirect(d.Id, savedVol * 100f);
                    }
                }

                OutputDeviceCombo.Items.Add(d.Name);
                if (d.IsDefault)
                {
                    selOutIdx = i;
                    _currentOutputDevice = d;
                }
            }
            if (_outputDevices.Count > 0)
            {
                OutputDeviceCombo.SelectedIndex = selOutIdx;
            }

            InputDeviceCombo.Items.Clear();
            int selInIdx = 0;
            for (int i = 0; i < _inputDevices.Count; i++)
            {
                var d = _inputDevices[i];
                InputDeviceCombo.Items.Add(d.Name);
                if (d.IsDefault)
                {
                    selInIdx = i;
                    _currentInputDevice = d;
                }
            }
            if (_inputDevices.Count > 0)
            {
                InputDeviceCombo.SelectedIndex = selInIdx;
            }

            UpdateMasterControls();
            UpdateInputControls();

            // 既定デバイスのセッション取得と展開パネルのセッション取得を並列実行
            if (_currentOutputDevice != null)
            {
                string devId = _currentOutputDevice.Id;
                var sessionsTask = System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeSessions(devId));
                var expandedTask = _isExpanded ? RefreshExpandedDevicesAsync() : System.Threading.Tasks.Task.CompletedTask;

                await System.Threading.Tasks.Task.WhenAll(sessionsTask, expandedTask);

                var sessions = sessionsTask.Result;
                _cachedSessions = sessions;
                RenderAppSessions(sessions);
            }
            else if (_isExpanded)
            {
                await RefreshExpandedDevicesAsync();
            }
        }
        catch { }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void UpdateMasterControls()
    {
        if (_currentOutputDevice == null) return;

        int vol = (int)Math.Round(_currentOutputDevice.Volume * 100f);
        // ユーザーがドラッグ・操作中でなければスライダー値をスムーズに更新
        if (!MasterVolumeSlider.IsMouseCaptureWithin && !MasterVolumeSlider.IsFocused)
        {
            _isUpdatingUi = true;
            try
            {
                MasterVolumeSlider.Value = vol;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
        bool isMuted = _currentOutputDevice.IsMuted || vol == 0;
        MasterMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.SpeakerOff24 : SymbolRegular.Speaker224);
        MasterMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isMuted ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
    }

    private void UpdateInputControls()
    {
        if (_currentInputDevice == null) return;
        int vol = (int)Math.Round(_currentInputDevice.Volume * 100f);
        if (!InputVolumeSlider.IsMouseCaptureWithin && !InputVolumeSlider.IsFocused)
        {
            _isUpdatingUi = true;
            try
            {
                InputVolumeSlider.Value = vol;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
        bool isMuted = _currentInputDevice.IsMuted || vol == 0;
        MicMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.MicOff24 : SymbolRegular.Mic24);
        MicMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isMuted ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
    }

    private void RenderAppSessions(List<SafeAudioSession> sessions)
    {
        AppSessionsPanel.Children.Clear();
        _sessionMeters.Clear();
        var toggleSliderStyle = (Style)FindResource("ToggleSliderStyle");
        var settings = _settingsAccessor();

        string currentDevName = _currentOutputDevice?.Name ?? "";
        if (string.IsNullOrEmpty(currentDevName) || currentDevName == "Default")
        {
            currentDevName = AudioDeviceHelper.GetDefaultDeviceName() ?? "";
        }
        if (string.IsNullOrEmpty(currentDevName))
        {
            currentDevName = _outputDevices.FirstOrDefault(d => d.IsDefault)?.Name 
                             ?? _outputDevices.FirstOrDefault()?.Name 
                             ?? "";
        }

        string effectiveDevName = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(currentDevName);

        foreach (var session in sessions)
        {
            // 保存済み音量の復元 (同アプリでもデバイスごとに記憶、FxSoundの裏デバイスも区別)
            string volKey = !string.IsNullOrEmpty(effectiveDevName) ? $"{effectiveDevName}_{session.DisplayName}" : "";
            string legacyVolKey = !string.IsNullOrEmpty(currentDevName) ? $"{currentDevName}_{session.DisplayName}" : "";
            bool hasSavedVol = false;
            float savedVol = 0f;

            if (!string.IsNullOrEmpty(volKey) && settings.AppVolumes.TryGetValue(volKey, out savedVol))
            {
                hasSavedVol = true;
            }
            else if (!string.IsNullOrEmpty(legacyVolKey) && settings.AppVolumes.TryGetValue(legacyVolKey, out savedVol))
            {
                hasSavedVol = true;
            }
            else if (!session.DisplayName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
            {
                // デバイス名取得タイミング等の差異による未登録初期音量の上書きを防止:
                // (ただし FxSound 等のデバイス個別セッションは他デバイスの音量を絶対に引き継がない)
                var fallbackEntry = settings.AppVolumes.FirstOrDefault(kvp => kvp.Key.EndsWith($"_{session.DisplayName}", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(fallbackEntry.Key))
                {
                    savedVol = fallbackEntry.Value;
                    hasSavedVol = true;
                }
            }

            if (hasSavedVol)
            {
                session.Volume = savedVol;
                var ctrls = session.Controls.Count > 0 ? session.Controls : (session.Control != null ? new List<AudioSessionControl> { session.Control } : new List<AudioSessionControl>());
                foreach (var ctrl in ctrls)
                {
                    try { ctrl.SimpleAudioVolume.Volume = savedVol; } catch { }
                }
            }
            else if (!session.DisplayName.Contains("FxSound", StringComparison.OrdinalIgnoreCase))
            {
                // 本当に音量が未登録の新規アプリの場合のみ、デフォルト音量 (初期値: 30%) を設定
                float defVol = Math.Clamp(settings.DefaultAppVolumePercent / 100.0f, 0.0f, 1.0f);
                session.Volume = defVol;
                var ctrls = session.Controls.Count > 0 ? session.Controls : (session.Control != null ? new List<AudioSessionControl> { session.Control } : new List<AudioSessionControl>());
                foreach (var ctrl in ctrls)
                {
                    try { ctrl.SimpleAudioVolume.Volume = defVol; } catch { }
                }
            }

            var card = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("ControlElevationBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 3)
            };

            var mainContainer = new StackPanel();

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var capturedSession = session;

            // アイコンコンテナ（アイコン ＋ 複数セッション時の展開バッジを重ねて配置）
            var iconContainer = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };

            bool isJa = SanmiToys.Core.Services.LocalizationService.Instance.EffectiveLanguageCode == "ja";

            // アプリアイコン（ホバーでアプリ名表示、クリックでミュート切替）
            var iconBtn = new Button
            {
                Appearance = ControlAppearance.Transparent,
                Padding = new Thickness(0),
                Width = 24,
                Height = 24,
                Foreground = System.Windows.Media.Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                ToolTip = $"{session.DisplayName} {(isJa ? "(クリックでミュート切替)" : "(Click to mute)")}"
            };

            bool isSysSound = session.ProcessId == 0 ||
                              session.DisplayName == "システム サウンド" ||
                              session.DisplayName == "System Sounds" ||
                              session.DisplayName.Contains("System Sound", StringComparison.OrdinalIgnoreCase) ||
                              session.DisplayName.Contains("システム", StringComparison.OrdinalIgnoreCase) ||
                              session.DisplayName.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                              session.DisplayName.Contains("audiodg", StringComparison.OrdinalIgnoreCase);

            FrameworkElement iconVisual;
            if (isSysSound)
            {
                iconVisual = CreateSystemSoundIcon();
            }
            else if (session.Icon != null)
            {
                iconVisual = new Image { Source = session.Icon, Width = 20, Height = 20 };
            }
            else
            {
                iconVisual = CreateGenericAppIcon();
            }
            iconVisual.Opacity = session.IsMuted ? 0.35 : 1.0;
            iconBtn.Content = iconVisual;

            iconBtn.Click += (s, e) =>
            {
                var targetControls = capturedSession.Controls.Count > 0 ? capturedSession.Controls : (capturedSession.Control != null ? new List<AudioSessionControl> { capturedSession.Control } : new List<AudioSessionControl>());
                if (targetControls.Count > 0)
                {
                    try
                    {
                        bool nextMute = !targetControls[0].SimpleAudioVolume.Mute;
                        foreach (var ctrl in targetControls)
                        {
                            try { ctrl.SimpleAudioVolume.Mute = nextMute; } catch { }
                        }
                        iconVisual.Opacity = nextMute ? 0.35 : 1.0;
                    }
                    catch { }
                }
            };
            iconContainer.Children.Add(iconBtn);

            var sliderContainer = new Grid { Height = 26, Margin = new Thickness(4, 0, 0, 0) };

            var meterBar = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush"),
                Opacity = 0.45,
                CornerRadius = new CornerRadius(3),
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(21, 0, 21, 0),
                Width = 0
            };
            sliderContainer.Children.Add(meterBar);

            var slider = new Slider 
            { 
                Minimum = 0, 
                Maximum = 100, 
                Value = session.Volume * 100, 
                VerticalAlignment = VerticalAlignment.Center,
                Style = toggleSliderStyle,
                ToolTip = $"{session.DisplayName}"
            };

            slider.ValueChanged += (s, e) =>
            {
                float newVol = (float)(slider.Value / 100.0);
                if (newVol > 0) iconVisual.Opacity = 1.0;

                // デバイス別に確実に音量を保存 (FxSoundの裏デバイスも区別)
                var curSettings = _settingsAccessor();
                string effectiveKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(currentDevName);
                string devKey = $"{effectiveKey}_{capturedSession.DisplayName}";
                curSettings.AppVolumes[devKey] = newVol;
                SwiftVolumeSettingsHelper.SaveSettingsDebounced(curSettings);

                System.Threading.Tasks.Task.Run(() =>
                {
                    var targetControls = capturedSession.Controls.Count > 0 ? capturedSession.Controls : (capturedSession.Control != null ? new List<AudioSessionControl> { capturedSession.Control } : new List<AudioSessionControl>());
                    if (targetControls.Count > 0)
                    {
                        foreach (var ctrl in targetControls)
                        {
                            try
                            {
                                ctrl.SimpleAudioVolume.Volume = newVol;
                                if (newVol > 0 && ctrl.SimpleAudioVolume.Mute)
                                {
                                    ctrl.SimpleAudioVolume.Mute = false;
                                }
                            }
                            catch { }
                        }
                    }
                });
            };
            sliderContainer.Children.Add(slider);

            Grid.SetColumn(sliderContainer, 1);
            grid.Children.Add(sliderContainer);

            _sessionMeters.Add(new SessionMeterItem
            {
                Session = capturedSession,
                MeterBar = meterBar,
                Container = sliderContainer,
                SmoothedPeak = 0f
            });

            // 子セッション展開（同一プロセスのセッションが複数ある場合、アイコン上に小さな展開ボタンを配置）
            if (session.ChildSessions.Count > 1)
            {
                var expandBadge = new Border
                {
                    Width = 13,
                    Height = 13,
                    CornerRadius = new CornerRadius(6.5),
                    Background = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, -1, -1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = isJa ? $"隠れているセッションを展開/折りたたみ ({session.ChildSessions.Count - 1}個)" : $"Expand / Collapse hidden sessions ({session.ChildSessions.Count - 1})"
                };
                var expandIcon = new SymbolIcon
                {
                    Symbol = SymbolRegular.ChevronDown12,
                    FontSize = 9,
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                expandBadge.Child = expandIcon;

                var childContainer = new StackPanel
                {
                    Visibility = Visibility.Collapsed,
                    Margin = new Thickness(28, 4, 0, 2)
                };

                // もともと隠れていたもの（2つ目以降）だけを下にぶら下げる
                foreach (var child in session.ChildSessions.Skip(1))
                {
                    var childRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    childRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    childRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var subIcon = new SymbolIcon
                    {
                        Symbol = SymbolRegular.ArrowRight16,
                        FontSize = 12,
                        Foreground = System.Windows.Media.Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(subIcon, 0);
                    childRow.Children.Add(subIcon);

                    var childSliderContainer = new Grid { Height = 26 };
                    var childMeterBar = new Border
                    {
                        Background = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush"),
                        Opacity = 0.45,
                        CornerRadius = new CornerRadius(3),
                        Height = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        Margin = new Thickness(21, 0, 21, 0),
                        Width = 0
                    };
                    childSliderContainer.Children.Add(childMeterBar);

                    var childSlider = new Slider
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = child.Volume * 100,
                        VerticalAlignment = VerticalAlignment.Center,
                        Style = toggleSliderStyle,
                        ToolTip = $"{child.DisplayName} (PID: {child.ProcessId})"
                    };
                    var capturedChild = child;
                    childSlider.ValueChanged += (cs, ce) =>
                    {
                        if (capturedChild.Control != null)
                        {
                            try
                            {
                                float cv = (float)(childSlider.Value / 100.0);
                                capturedChild.Control.SimpleAudioVolume.Volume = cv;
                            }
                            catch { }
                        }
                    };
                    childSliderContainer.Children.Add(childSlider);

                    Grid.SetColumn(childSliderContainer, 1);
                    childRow.Children.Add(childSliderContainer);
                    childContainer.Children.Add(childRow);

                    _sessionMeters.Add(new SessionMeterItem
                    {
                        Session = capturedChild,
                        MeterBar = childMeterBar,
                        Container = childSliderContainer,
                        SmoothedPeak = 0f
                    });
                }

                expandBadge.MouseLeftButtonUp += (eb, ee) =>
                {
                    bool isExp = childContainer.Visibility == Visibility.Visible;
                    childContainer.Visibility = isExp ? Visibility.Collapsed : Visibility.Visible;
                    expandIcon.Symbol = isExp ? SymbolRegular.ChevronDown12 : SymbolRegular.ChevronUp12;
                    ee.Handled = true;
                };

                iconContainer.Children.Add(expandBadge);

                Grid.SetColumn(iconContainer, 0);
                grid.Children.Add(iconContainer);

                mainContainer.Children.Add(grid);
                mainContainer.Children.Add(childContainer);
            }
            else
            {
                Grid.SetColumn(iconContainer, 0);
                grid.Children.Add(iconContainer);
                mainContainer.Children.Add(grid);
            }

            card.Child = mainContainer;
            AppSessionsPanel.Children.Add(card);
        }
    }

    private async Task RefreshExpandedDevicesAsync()
    {
        var nonDefaultDevices = _outputDevices
            .Where(dev => dev.Id != _currentOutputDevice?.Id)
            .ToList();

        // 展開時のウィンドウサイズを他デバイス数に合わせて正しく調整
        double targetW = Math.Min(370 + (nonDefaultDevices.Count * 280), SystemParameters.WorkArea.Width - 24);
        if (Math.Abs(this.Width - targetW) > 1.0)
        {
            this.Width = targetW;
            UpdateWindowPosition();
        }

        var sessionTasks = new Dictionary<string, Task<List<SafeAudioSession>>>();
        foreach (var dev in nonDefaultDevices)
        {
            string devId = dev.Id;
            sessionTasks[devId] = System.Threading.Tasks.Task.Run(() =>
            {
                try { return _deviceService.GetSafeSessions(devId); }
                catch { return new List<SafeAudioSession>(); }
            });
        }

        try
        {
            await System.Threading.Tasks.Task.WhenAll(sessionTasks.Values);
        }
        catch { }

        ExpandedDevicesPanel.Children.Clear();
        _expandedDeviceSliders.Clear();
        var toggleSliderStyle = (Style)FindResource("ToggleSliderStyle");

        foreach (var dev in nonDefaultDevices)
        {
            var devBorder = new Border
            {
                Width = 270,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                Background = (System.Windows.Media.Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("ControlElevationBorderBrush"),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel();

            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock 
            { 
                Text = dev.Name, 
                FontWeight = FontWeights.SemiBold, 
                FontSize = 13, 
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 170
            };
            titleSp.Children.Add(title);

            bool isJa = SanmiToys.Core.Services.LocalizationService.Instance.EffectiveLanguageCode == "ja";

            if (dev.IsDefault)
            {
                var defaultBadge = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                defaultBadge.Child = new TextBlock
                {
                    Text = isJa ? "既定" : "Default",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White
                };
                titleSp.Children.Add(defaultBadge);
            }

            headerGrid.Children.Add(titleSp);

            var setDefaultBtn = new Button { Content = isJa ? "既定にする" : "Set as Default", Appearance = ControlAppearance.Secondary, FontSize = 11, Padding = new Thickness(6, 2, 6, 2) };
            if (dev.IsDefault) setDefaultBtn.Visibility = Visibility.Collapsed;
            string capturedId = dev.Id;
            setDefaultBtn.Click += (s, e) =>
            {
                AudioDeviceHelper.PreApplyDeviceVolume(capturedId);
                PolicyConfig.SetDefaultDevice(capturedId);
                RefreshData();
            };
            Grid.SetColumn(setDefaultBtn, 1);
            headerGrid.Children.Add(setDefaultBtn);
            sp.Children.Add(headerGrid);

            var expDevSettings = _settingsAccessor();
            float initialDevVol = dev.Volume;

            var volSlider = new Slider 
            { 
                Minimum = 0, 
                Maximum = 100, 
                Value = initialDevVol * 100, 
                Margin = new Thickness(0, 0, 0, 8),
                Style = toggleSliderStyle
            };
            string targetDevId = dev.Id;
            string targetDevName = dev.Name;
            _expandedDeviceSliders[targetDevId] = volSlider;

            float lastVolChangeVal = -1f;
            volSlider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                float v = (float)volSlider.Value;
                if (Math.Abs(lastVolChangeVal - v) < 0.25f && v > 0 && v < 100) return;
                lastVolChangeVal = v;

                dev.Volume = v / 100f;
                var curSettings = _settingsAccessor();
                curSettings.DeviceMasterVolumes[targetDevName] = v / 100f;

                // 本当に連動している裏デバイスのみ判定 (例: FxSoundの出力先裏デバイス1)
                bool isLinked = IsDeviceLinkedWithDefault(dev);

                if (isLinked)
                {
                    _singleLinkedDeviceId = targetDevId;
                    if (_currentOutputDevice != null)
                    {
                        string fxKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(_currentOutputDevice.Name);
                        curSettings.DeviceMasterVolumes[fxKey] = v / 100f;
                    }
                    _isUpdatingUi = true;
                    try
                    {
                        MasterVolumeSlider.Value = v;
                        if (_currentOutputDevice != null) _currentOutputDevice.Volume = v / 100f;
                        bool isM = (int)Math.Round(v) == 0 || dev.IsMuted;
                        MasterMuteButton.Icon = new SymbolIcon(isM ? SymbolRegular.SpeakerOff24 : SymbolRegular.Speaker224);
                        MasterMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isM ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
                    }
                    finally
                    {
                        _isUpdatingUi = false;
                    }

                    // バックグラウンドで非同期スロットリング送信
                    QueueMasterVolumeUpdate(v);
                }
                else
                {
                    // 連動していない裏デバイスは完全独立（既定デバイスや他デバイスに一切影響を与えない）
                    _deviceService.SetDeviceVolumeDirect(targetDevId, v);
                }

                SwiftVolumeSettingsHelper.SaveSettingsDebounced(curSettings);
            };
            sp.Children.Add(volSlider);

            var appsTitle = new TextBlock { Text = isJa ? "アプリケーション音量" : "App Volume", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 4, 0, 6) };
            sp.Children.Add(appsTitle);

            List<SafeAudioSession> devSessions = new();
            if (sessionTasks.TryGetValue(dev.Id, out var sTask) && sTask.IsCompletedSuccessfully)
            {
                devSessions = sTask.Result ?? new List<SafeAudioSession>();
            }
            if (devSessions.Count == 0)
            {
                var noApp = new TextBlock { Text = isJa ? "再生中のアプリケーションはありません" : "No active audio sessions", FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush"), Margin = new Thickness(4, 4, 4, 4) };
                sp.Children.Add(noApp);
            }
            else
            {
                foreach (var s in devSessions)
                {
                    var appCard = new Border
                    {
                        Background = (System.Windows.Media.Brush)FindResource("CardBackgroundFillColorSecondaryBrush"),
                        BorderBrush = (System.Windows.Media.Brush)FindResource("ControlElevationBorderBrush"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(6, 3, 6, 3),
                        Margin = new Thickness(0, 0, 0, 3)
                    };

                    var appGrid = new Grid();
                    appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var capturedDevSession = s;

                    var appIconBtn = new Button
                    {
                        Appearance = ControlAppearance.Transparent,
                        Padding = new Thickness(2),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = $"{s.DisplayName} {(isJa ? "(クリックでミュート切替)" : "(Click to mute)")}"
                    };

                    bool isSysSound = s.ProcessId == 0 ||
                                      s.DisplayName == "システム サウンド" ||
                                      s.DisplayName == "System Sounds" ||
                                      s.DisplayName.Contains("System Sound", StringComparison.OrdinalIgnoreCase) ||
                                      s.DisplayName.Contains("システム", StringComparison.OrdinalIgnoreCase) ||
                                      s.DisplayName.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                                      s.DisplayName.Contains("audiodg", StringComparison.OrdinalIgnoreCase);

                    FrameworkElement appIconVisual;
                    if (isSysSound)
                    {
                        appIconVisual = CreateSystemSoundIcon();
                    }
                    else if (s.Icon != null)
                    {
                        appIconVisual = new Image { Source = s.Icon, Width = 20, Height = 20 };
                    }
                    else
                    {
                        appIconVisual = CreateGenericAppIcon();
                    }
                    appIconVisual.Opacity = s.IsMuted ? 0.35 : 1.0;
                    appIconBtn.Content = appIconVisual;

                    appIconBtn.Click += (btnSender, btnE) =>
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            var targetControls = capturedDevSession.Controls.Count > 0 ? capturedDevSession.Controls : (capturedDevSession.Control != null ? new List<AudioSessionControl> { capturedDevSession.Control } : new List<AudioSessionControl>());
                            if (targetControls.Count > 0)
                            {
                                try
                                {
                                    bool next = !targetControls[0].SimpleAudioVolume.Mute;
                                    foreach (var ctrl in targetControls)
                                    {
                                        try { ctrl.SimpleAudioVolume.Mute = next; } catch { }
                                    }
                                    Dispatcher.InvokeAsync(() =>
                                    {
                                        appIconVisual.Opacity = next ? 0.35 : 1.0;
                                    });
                                }
                                catch { }
                            }
                        });
                    };
                    Grid.SetColumn(appIconBtn, 0);
                    appGrid.Children.Add(appIconBtn);

                    // 拡張パネルでも保存済み音量を復元
                    var expSettings = _settingsAccessor();
                    string expDevKey = $"{dev.Name}_{s.DisplayName}";
                    bool expHasSavedVol = false;
                    float expSavedVol = 0f;

                    if (expSettings.AppVolumes.TryGetValue(expDevKey, out expSavedVol))
                    {
                        expHasSavedVol = true;
                    }

                    if (expHasSavedVol)
                    {
                        s.Volume = expSavedVol;
                        var ctrls = s.Controls.Count > 0 ? s.Controls : (s.Control != null ? new List<AudioSessionControl> { s.Control } : new List<AudioSessionControl>());
                        foreach (var ctrl in ctrls)
                        {
                            try { ctrl.SimpleAudioVolume.Volume = expSavedVol; } catch { }
                        }
                    }
                    // 保存済み音量がない場合（特に連動先デバイス上の FxSound セッション 100% 等）は、
                    // セッション本来の音量を勝手に書き換えずそのまま維持！

                    var sSlider = new Slider 
                    { 
                        Minimum = 0, 
                        Maximum = 100, 
                        Value = s.Volume * 100, 
                        Margin = new Thickness(4, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Style = toggleSliderStyle,
                        ToolTip = $"{s.DisplayName}"
                    };
                    sSlider.ValueChanged += (sSender, sE) =>
                    {
                        float newVol = (float)(sSlider.Value / 100.0);
                        if (newVol > 0) appIconVisual.Opacity = 1.0;

                        var curSettings = _settingsAccessor();
                        string devVolKey = $"{dev.Name}_{capturedDevSession.DisplayName}";
                        curSettings.AppVolumes[devVolKey] = newVol;
                        SwiftVolumeSettingsHelper.SaveSettingsDebounced(curSettings);

                        System.Threading.Tasks.Task.Run(() =>
                        {
                            var targetControls = capturedDevSession.Controls.Count > 0 ? capturedDevSession.Controls : (capturedDevSession.Control != null ? new List<AudioSessionControl> { capturedDevSession.Control } : new List<AudioSessionControl>());
                            if (targetControls.Count > 0)
                            {
                                foreach (var ctrl in targetControls)
                                {
                                    try
                                    {
                                        ctrl.SimpleAudioVolume.Volume = newVol;
                                        if (newVol > 0 && ctrl.SimpleAudioVolume.Mute)
                                        {
                                            ctrl.SimpleAudioVolume.Mute = false;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        });
                    };
                    Grid.SetColumn(sSlider, 1);
                    appGrid.Children.Add(sSlider);

                    appCard.Child = appGrid;
                    sp.Children.Add(appCard);
                }
            }

            devBorder.Child = sp;
            ExpandedDevicesPanel.Children.Add(devBorder);
        }
    }

    private void OnToggleExpandClicked(object sender, RoutedEventArgs e)
    {
        _isExpanded = !_isExpanded;
        ExpandedPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Foreground = (System.Windows.Media.Brush)FindResource(_isExpanded ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        ShowAtCursorOrTray();
    }

    private float _lastMasterChangedVol = -1f;

    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || _currentOutputDevice == null) return;
        float vol = (float)MasterVolumeSlider.Value;
        if (Math.Abs(_lastMasterChangedVol - vol) < 0.15f && vol > 0 && vol < 100) return;
        _lastMasterChangedVol = vol;

        _currentOutputDevice.Volume = vol / 100f;

        // UI 表示は 0ms 即時更新（完全 60fps 以上のヌルヌル追従）
        bool isMuted = (int)Math.Round(vol) == 0 || _currentOutputDevice.IsMuted;
        MasterMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.SpeakerOff24 : SymbolRegular.Speaker224);
        MasterMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isMuted ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");

        // 連動裏スライダーも UI 上で 0ms 即時追従
        SyncLinkedSliders(vol);

        var settings = _settingsAccessor();
        string effectiveKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(_currentOutputDevice.Name);
        settings.DeviceMasterVolumes[effectiveKey] = vol / 100f;
        settings.DeviceMasterVolumes[_currentOutputDevice.Name] = vol / 100f;

        // 連動している裏デバイス自体の保存音量も同期
        foreach (var dev in _outputDevices)
        {
            if (dev.Id != _currentOutputDevice.Id && IsDeviceLinkedWithDefault(dev))
            {
                settings.DeviceMasterVolumes[dev.Name] = vol / 100f;
            }
        }

        SwiftVolumeSettingsHelper.SaveSettingsDebounced(settings);

        // 重い COM 呼び出しはバックグラウンドキューでスロットリング送信（UIスレッドを完全開放）
        QueueMasterVolumeUpdate(vol);
    }

    private void OnMasterMuteClicked(object sender, RoutedEventArgs e)
    {
        var (_, isMuted) = AudioDeviceHelper.ToggleMute();
        MasterMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.SpeakerOff24 : SymbolRegular.Speaker224);
    }

    private float _lastInputChangedVol = -1f;

    private void OnInputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || _currentInputDevice == null) return;
        float vol = (float)InputVolumeSlider.Value;
        if (Math.Abs(_lastInputChangedVol - vol) < 0.15f && vol > 0 && vol < 100) return;
        _lastInputChangedVol = vol;

        _currentInputDevice.Volume = vol / 100f;

        // UI 表示・ミュートアイコンは 0ms 即時更新（シルクのようにスムーズな追従）
        bool isMuted = (int)Math.Round(vol) == 0 || _currentInputDevice.IsMuted;
        MicMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.MicOff24 : SymbolRegular.Mic24);
        MicMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isMuted ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");

        // 重い COM 呼び出しはバックグラウンドキューでスロットリング送信（UIスレッドを完全開放）
        QueueInputVolumeUpdate(vol, _currentInputDevice.Id);
    }

    private void OnMicMuteClicked(object sender, RoutedEventArgs e)
    {
        bool isMuted = AudioDeviceHelper.ToggleInputMute();
        MicMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.MicOff24 : SymbolRegular.Mic24);
    }

    private async void OnOutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        int idx = OutputDeviceCombo.SelectedIndex;
        if (idx >= 0 && idx < _outputDevices.Count)
        {
            var target = _outputDevices[idx];
            _isUpdatingUi = true;
            try
            {
                AudioDeviceHelper.PreApplyDeviceVolume(target.Id);
                PolicyConfig.SetDefaultDevice(target.Id);
                _currentOutputDevice = target;

                string effKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(target.Name);
                var svSettings = _settingsAccessor();
                if (svSettings.DeviceMasterVolumes.TryGetValue(effKey, out float savedVol) ||
                    svSettings.DeviceMasterVolumes.TryGetValue(target.Name, out savedVol))
                {
                    target.Volume = savedVol;
                }

                UpdateMasterControls();

                // デバイスの切り替えを少し待機してからセッションを取得
                await System.Threading.Tasks.Task.Delay(100);
                string devId = target.Id;
                var sessions = await System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeSessions(devId));
                RenderAppSessions(sessions);
                if (_isExpanded) await RefreshExpandedDevicesAsync();
            }
            catch { }
            finally
            {
                _isUpdatingUi = false;
            }
        }
    }

    private void OnInputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        int idx = InputDeviceCombo.SelectedIndex;
        if (idx >= 0 && idx < _inputDevices.Count)
        {
            var target = _inputDevices[idx];
            _isUpdatingUi = true;
            try
            {
                PolicyConfig.SetDefaultDevice(target.Id);
                _currentInputDevice = target;
                UpdateInputControls();
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
    }

    private static FrameworkElement CreateSystemSoundIcon()
    {
        return new System.Windows.Shapes.Path
        {
            Width = 18,
            Height = 18,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Fill = System.Windows.Media.Brushes.White,
            Data = System.Windows.Media.Geometry.Parse("M12.44 2.22a1.25 1.25 0 0 0-1.39.26L6.8 6.75H4.25A2.25 2.25 0 0 0 2 9v6a2.25 2.25 0 0 0 2.25 2.25h2.55l4.25 4.27a1.25 1.25 0 0 0 2.15-.88V3.36a1.25 1.25 0 0 0-.76-1.14zm4.47 5.17a1 1 0 0 1 1.41.07 7 7 0 0 1 0 9.08 1 1 0 1 1-1.48-1.34 5 5 0 0 0 0-6.4 1 1 0 0 1 .07-1.41zm2.83-2.83a1 1 0 0 1 1.41.07 11 11 0 0 1 0 14.74 1 1 0 1 1-1.48-1.34 9 9 0 0 0 0-12.06 1 1 0 0 1 .07-1.41z")
        };
    }

    private static FrameworkElement CreateGenericAppIcon()
    {
        return new System.Windows.Shapes.Path
        {
            Width = 18,
            Height = 18,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Fill = System.Windows.Media.Brushes.White,
            Data = System.Windows.Media.Geometry.Parse("M4 3a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2H4zm0 2h16v14H4V5zm2 2v2h2V7H6zm4 0v2h8V7h-8zm-4 4v2h2v-2H6zm4 0v2h8v-2h-8zm-4 4v2h2v-2H6zm4 0v2h8v-2h-8z")
        };
    }
}
