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
using Point = System.Windows.Point;
using MouseButtonState = System.Windows.Input.MouseButtonState;

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
    private readonly DispatcherTimer _sessionWatchTimer;
    private int _isSessionWatchRunning = 0;
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
    private readonly Dictionary<string, Slider> _appSliders = new();
    private DateTime _lastUserAppSliderInteraction = DateTime.MinValue;
    private DateTime _lastMasterUserInteraction = DateTime.MinValue;
    private DateTime _lastInputUserInteraction = DateTime.MinValue;
    private readonly Dictionary<string, Slider> _expandedDeviceSliders = new();
    private string? _singleLinkedDeviceId;
    private bool _isSelfDraggingMaster = false;
    private float _targetMasterVol = -1f;
    private int _masterVolWorkerRunning = 0;
    private float _targetInputVol = -1f;
    private int _inputVolWorkerRunning = 0;

    private class SessionMeterItem
    {
        public SafeAudioSession Session { get; set; } = null!;
        public Border MeterBar { get; set; } = null!;
        public Grid Container { get; set; } = null!;
        public float SmoothedPeak { get; set; }
    }

    private readonly List<SessionMeterItem> _sessionMeters = new();
    private readonly HashSet<AudioSessionControl> _faultedControls = new();
    private Border? _draggedCard;
    private bool _isDraggingSession = false;
    private Point _dragStartPos;
    private double? _preExpandLeft;

    public MixerWindow(Func<SwiftVolumeSettings> settingsAccessor)
    {
        InitializeComponent();
        _settingsAccessor = settingsAccessor;

        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (hwnd != IntPtr.Zero)
        {
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProcHook);
        }

        // 起動時にバックグラウンドでデバイスとセッション情報を事前キャッシュ（初回表示 0ms 化）
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // 除外対象アプリの過去の保存音量キーをパージし、全デバイスで音量を100%に復帰
                var settings = _settingsAccessor();
                SwiftVolumeSettingsHelper.PurgeExcludedAppVolumes(settings);
                SwiftVolumeSettingsHelper.ResetAllVolumeData(settings);
                _deviceService.ResetExcludedSessionsVolumeToDefault();

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
                        _smoothedOutputPeak = _smoothedOutputPeak + (raw - _smoothedOutputPeak) * 0.65f;
                    }
                    else
                    {
                        // リリース（美しく余韻を残して滑らかにフェードアウト）
                        _smoothedOutputPeak = Math.Max(0f, _smoothedOutputPeak * 0.90f - 0.002f);
                    }
                    OutputPeakLevel = _smoothedOutputPeak;
                }
                if (_currentInputDevice != null)
                {
                    float raw = _meteringService.GetPeakLevel(_currentInputDevice.Id);
                    raw = Math.Min(1.0f, raw * 1.5f);
                    if (raw > _smoothedInputPeak)
                    {
                        _smoothedInputPeak = _smoothedInputPeak + (raw - _smoothedInputPeak) * 0.65f;
                    }
                    else
                    {
                        _smoothedInputPeak = Math.Max(0f, _smoothedInputPeak * 0.90f - 0.002f);
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
                            if (_faultedControls.Contains(ctrl)) continue;
                            try
                            {
                                if (ctrl.AudioMeterInformation != null)
                                {
                                    float v = ctrl.AudioMeterInformation.MasterPeakValue;
                                    if (v > raw) raw = v;
                                }
                            }
                            catch
                            {
                                _faultedControls.Add(ctrl);
                            }
                        }
                    }

                    // 感度を従来の半分程度（3.0 -> 1.5）に調整して過敏な振り切れを防止
                    raw = Math.Min(1.0f, raw * 1.5f);
                    if (raw > item.SmoothedPeak)
                    {
                        item.SmoothedPeak = item.SmoothedPeak + (raw - item.SmoothedPeak) * 0.65f;
                    }
                    else
                    {
                        // 下降時に心地よい余韻（滑らかなフォールオフ）を付加
                        item.SmoothedPeak = Math.Max(0f, item.SmoothedPeak * 0.90f - 0.002f);
                    }

                    double maxW = Math.Max(0, item.Container.ActualWidth - 42);
                    item.MeterBar.Width = maxW * item.SmoothedPeak;
                }
            }
            catch { }
        };

        _sessionWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _sessionWatchTimer.Tick += (s, e) =>
        {
            if (!this.IsVisible || _currentOutputDevice == null) return;
            if (_isDraggingSession || _isUpdatingUi) return;

            if (Interlocked.CompareExchange(ref _isSessionWatchRunning, 1, 0) != 0) return;

            string devId = _currentOutputDevice.Id;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var freshSessions = _deviceService.GetSafeSessions(devId);
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            if (!this.IsVisible || _isDraggingSession) return;
                            _cachedSessions = freshSessions;
                            UpdateAppSessionsIncremental(freshSessions);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _isSessionWatchRunning, 0);
                        }
                    });
                }
                catch
                {
                    Interlocked.Exchange(ref _isSessionWatchRunning, 0);
                }
            });
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
            if (this.IsVisible)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (this.IsVisible)
                    {
                        AdjustPositionOnSizeChanged();
                    }
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
        };

        AudioDeviceHelper.DefaultDeviceChanged += () =>
        {
            Dispatcher.InvokeAsync(() => RefreshDataAsync());
        };

        MasterVolumeSlider.PreviewMouseDown += (s, e) =>
        {
            _isSelfDraggingMaster = true;
            _lastMasterUserInteraction = DateTime.UtcNow;
        };
        MasterVolumeSlider.PreviewMouseUp += (s, e) =>
        {
            _isSelfDraggingMaster = false;
            _lastMasterUserInteraction = DateTime.UtcNow;
        };
        MasterVolumeSlider.PreviewMouseMove += (s, e) =>
        {
            if (MasterVolumeSlider.IsMouseCaptureWithin || e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            {
                _isSelfDraggingMaster = true;
                _lastMasterUserInteraction = DateTime.UtcNow;
            }
        };
        MasterVolumeSlider.MouseLeave += (s, e) =>
        {
            if (!MasterVolumeSlider.IsMouseCaptureWithin && e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed)
            {
                _isSelfDraggingMaster = false;
            }
        };

        InputVolumeSlider.PreviewMouseDown += (s, e) => _lastInputUserInteraction = DateTime.UtcNow;
        InputVolumeSlider.PreviewMouseUp += (s, e) => _lastInputUserInteraction = DateTime.UtcNow;
        InputVolumeSlider.PreviewMouseMove += (s, e) =>
        {
            if (InputVolumeSlider.IsMouseCaptureWithin || e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            {
                _lastInputUserInteraction = DateTime.UtcNow;
            }
        };

        AudioDeviceHelper.MasterVolumeChanged += (vol, muted) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isSelfDraggingMaster || 
                    MasterVolumeSlider.IsMouseCaptureWithin || 
                    MasterVolumeSlider.IsFocused ||
                    (DateTime.UtcNow - _lastMasterUserInteraction).TotalMilliseconds < 800)
                {
                    return;
                }

                if (_currentOutputDevice != null && _currentOutputDevice.IsDefault)
                {
                    _currentOutputDevice.Volume = vol / 100f;
                    _currentOutputDevice.IsMuted = muted;
                    UpdateMasterControls();

                    SyncLinkedSliders(vol);
                }
            });
        };
    }

    private void StartFxSoundWatcher()
    {
        // FxSound のデバイス監視・音量同期は SwiftVolumeTrayManager で一元管理され、
        // 変更時は SwiftVolumeTrayManager から _mixerWindow.RefreshDataAsync() が呼ばれるため、
        // MixerWindow 側での重複監視・UIスレッド負荷を回避。
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

        // 初期化前の残像表示を防ぐため透明度をリセット
        this.Opacity = 0;

        // デバイスが未解決またはIDが空の場合は即座に既定デバイスを割り当て（初回表示時0ms同期解決）
        if (_currentOutputDevice == null || string.IsNullOrEmpty(_currentOutputDevice.Id))
        {
            string defName = AudioDeviceHelper.GetDefaultDeviceName();
            _currentOutputDevice = _outputDevices.FirstOrDefault(d => d.IsDefault || (!string.IsNullOrEmpty(defName) && d.Name == defName))
                                   ?? _outputDevices.FirstOrDefault();
            if (_currentOutputDevice == null || string.IsNullOrEmpty(_currentOutputDevice.Id))
            {
                try
                {
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    using var defDev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                    if (defDev != null)
                    {
                        _currentOutputDevice = new SafeDeviceInfo
                        {
                            Id = defDev.ID,
                            Name = defDev.FriendlyName,
                            IsDefault = true
                        };
                    }
                }
                catch { }
            }
            if (_currentOutputDevice == null && !string.IsNullOrEmpty(defName))
            {
                _currentOutputDevice = new SafeDeviceInfo { Name = defName, IsDefault = true };
            }
        }

        // 実際のマスター音量（出力）を即座に取得してスライダー・テキストを先行同期（表示ズレを完全解消）
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

        // マイク（入力）音量も即座に先行同期（前回の値の残像表示を防止）
        try
        {
            float curInVol = AudioDeviceHelper.GetInputVolume();
            bool curInMuted = AudioDeviceHelper.GetIsInputMuted();
            if (_currentInputDevice != null && _currentInputDevice.IsDefault)
            {
                _currentInputDevice.Volume = curInVol / 100f;
                _currentInputDevice.IsMuted = curInMuted;
            }
            _isUpdatingUi = true;
            try
            {
                int inVInt = (int)Math.Round(curInVol);
                InputVolumeSlider.Value = inVInt;
                bool isMuted = curInMuted || inVInt == 0;
                MicMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.MicOff24 : SymbolRegular.Mic24);
                MicMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isMuted ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
        catch { }

        // 縦解像度に応じた最大サイズ設定 (カーソルが存在するモニターのワークエリア高さを動的解決)
        double workAreaH = SystemParameters.WorkArea.Height;
        SwiftVolumeNativeMethods.GetCursorPos(out var curPos);
        IntPtr hCurMon = SwiftVolumeNativeMethods.MonitorFromPoint(curPos, SwiftVolumeNativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hCurMon != IntPtr.Zero)
        {
            var mi = new SwiftVolumeNativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<SwiftVolumeNativeMethods.MONITORINFO>() };
            if (SwiftVolumeNativeMethods.GetMonitorInfo(hCurMon, ref mi))
            {
                double dY = 1.0;
                if (SwiftVolumeNativeMethods.GetDpiForMonitor(hCurMon, SwiftVolumeNativeMethods.MDT_EFFECTIVE_DPI, out _, out uint dpiY) == 0)
                {
                    dY = dpiY / 96.0;
                }
                workAreaH = (mi.rcWork.Bottom - mi.rcWork.Top) / dY;
            }
        }
        AppSessionsScrollViewer.MaxHeight = Math.Max(160, workAreaH - 220);
        this.MaxHeight = Math.Max(260, workAreaH - 20);

        double otherDevsCount = Math.Max(0, _outputDevices.Count - 1);
        double targetW = _isExpanded ? Math.Min(370 + (otherDevsCount * 280), SystemParameters.WorkArea.Width - 24) : 370;
        this.Width = targetW;

        // 現在の出力デバイスの最新セッションを即時取得して初期描画（古いセッションや古い音量の残像表示を完全根絶）
        try
        {
            string devId = _currentOutputDevice?.Id ?? "";
            if (!string.IsNullOrEmpty(devId))
            {
                var sessions = _deviceService.GetSafeSessions(devId);
                _cachedSessions = sessions;
                RenderAppSessions(sessions);
            }
        }
        catch { }

        // レイアウトを事前に計測して正確な高さを確定
        try
        {
            this.Measure(new System.Windows.Size(targetW, workAreaH));
            this.UpdateLayout();
        }
        catch { }

        // 計測済みのサイズをもとに直ちに位置を適用
        UpdateWindowPosition();
        if (!_isExpanded)
        {
            _preExpandLeft = this.Left;
        }

        this.Show();
        this.Activate();
        this.Focus();
        this.Opacity = 1;

        // 表示直後にも実際のレンダリングサイズで位置を再調整
        UpdateWindowPosition();

        Dispatcher.InvokeAsync(() =>
        {
            if (this.IsVisible)
            {
                AdjustPositionOnSizeChanged();
            }
        }, DispatcherPriority.Loaded);

        StartFocusMonitor();
        _meterTimer.Start();
        _sessionWatchTimer.Start();

        var hwnd = new WindowInteropHelper(this).Handle;
        WindowPlacementHelper.ForceForeground(hwnd);

        // バックグラウンドで非同期にデバイス一覧・拡張パネルを整合
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
        double workAreaH = workBottom - workTop;

        double targetW = this.Width > 0 ? this.Width : 370;
        try
        {
            RootBorder.Measure(new System.Windows.Size(targetW, workAreaH));
        }
        catch { }

        double measuredH = RootBorder.DesiredSize.Height;
        double actualW = this.ActualWidth > 0 ? this.ActualWidth : (RootBorder.DesiredSize.Width > 0 ? RootBorder.DesiredSize.Width : targetW);
        double actualH = measuredH > 0 ? measuredH : (this.ActualHeight > 0 ? this.ActualHeight : 460);
        actualH = Math.Clamp(actualH, this.MinHeight, Math.Min(this.MaxHeight, workAreaH - 20));

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

        // Win32 API SetWindowPos で即座にOSレベルのウィンドウ位置を同期（WPF SizeToContentの競合を根絶）
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int physX = (int)Math.Round(finalLeft * dpiScaleX);
                int physY = (int)Math.Round(finalTop * dpiScaleY);
                SwiftVolumeNativeMethods.SetWindowPos(hwnd, IntPtr.Zero, physX, physY, 0, 0,
                    SwiftVolumeNativeMethods.SWP_NOSIZE | SwiftVolumeNativeMethods.SWP_NOZORDER | SwiftVolumeNativeMethods.SWP_NOACTIVATE);
            }
        }
        catch { }
    }

    private void AdjustPositionOnSizeChanged()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        IntPtr hMonitor = hwnd != IntPtr.Zero
            ? SwiftVolumeNativeMethods.MonitorFromWindow(hwnd, SwiftVolumeNativeMethods.MONITOR_DEFAULTTONEAREST)
            : IntPtr.Zero;

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

        if (hMonitor != IntPtr.Zero)
        {
            try
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
            catch { }
        }

        double workLeft = rcWork.Left / dpiScaleX;
        double workTop = rcWork.Top / dpiScaleY;
        double workRight = rcWork.Right / dpiScaleX;
        double workBottom = rcWork.Bottom / dpiScaleY;

        double workAreaH = workBottom - workTop;
        double actualW = this.ActualWidth > 0 ? this.ActualWidth : 370;
        double actualH = this.ActualHeight > 0 ? this.ActualHeight : 460;
        actualH = Math.Clamp(actualH, this.MinHeight, Math.Min(this.MaxHeight, workAreaH - 20));

        // タスクバーの位置判定
        bool tbTop = rcWork.Top > rcMonitor.Top;
        bool tbLeft = rcWork.Left > rcMonitor.Left;
        bool tbRight = rcWork.Right < rcMonitor.Right;

        double margin = 10.0;
        double currentLeft = this.Left;
        double targetTop = this.Top;

        if (tbTop)
        {
            // 上部タスクバー: 上端固定で下へ伸ばす
            targetTop = workTop + margin;
        }
        else if (tbLeft || tbRight)
        {
            // 左右タスクバー: 画面外に出ないようにクランプ
            if (targetTop + actualH > workBottom - margin)
            {
                targetTop = workBottom - actualH - margin;
            }
            if (targetTop < workTop + margin)
            {
                targetTop = workTop + margin;
            }
        }
        else
        {
            // 下部タスクバー（標準）: 底面をタスクバー上端に固定して上へ伸ばす！
            targetTop = workBottom - actualH - margin;
        }

        // 横位置（Left）はカーソル位置で再計算せず、現在の Left を維持！
        // 画面外にはみ出る場合のみクランプ
        if (currentLeft < workLeft + 8) currentLeft = workLeft + 8;
        if (currentLeft + actualW > workRight - 8) currentLeft = workRight - actualW - 8;
        if (targetTop < workTop + 8) targetTop = workTop + 8;
        if (targetTop + actualH > workBottom - 8) targetTop = workBottom - actualH - 8;

        this.Left = currentLeft;
        this.Top = targetTop;

        // Win32 API SetWindowPos で即座にOSレベルのウィンドウ位置を同期
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                int physX = (int)Math.Round(currentLeft * dpiScaleX);
                int physY = (int)Math.Round(targetTop * dpiScaleY);
                SwiftVolumeNativeMethods.SetWindowPos(hwnd, IntPtr.Zero, physX, physY, 0, 0,
                    SwiftVolumeNativeMethods.SWP_NOSIZE | SwiftVolumeNativeMethods.SWP_NOZORDER | SwiftVolumeNativeMethods.SWP_NOACTIVATE);
            }
        }
        catch { }
    }

    /// <summary>
    /// Win32 ウィンドウプロシージャフック。
    /// WPFの非同期レイアウト更新やセッション増加時にウィンドウがタスクバー下部に埋まらないよう、
    /// WM_WINDOWPOSCHANGING の段階でOSレベルで座標を強制補正する。
    /// </summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == SwiftVolumeNativeMethods.WM_WINDOWPOSCHANGING)
        {
            try
            {
                var wp = Marshal.PtrToStructure<SwiftVolumeNativeMethods.WINDOWPOS>(lParam);
                if ((wp.flags & SwiftVolumeNativeMethods.SWP_NOMOVE) == 0 || (wp.flags & SwiftVolumeNativeMethods.SWP_NOSIZE) == 0)
                {
                    IntPtr hMonitor = SwiftVolumeNativeMethods.MonitorFromWindow(hwnd, SwiftVolumeNativeMethods.MONITOR_DEFAULTTONEAREST);
                    if (hMonitor != IntPtr.Zero)
                    {
                        var mi = new SwiftVolumeNativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<SwiftVolumeNativeMethods.MONITORINFO>() };
                        if (SwiftVolumeNativeMethods.GetMonitorInfo(hMonitor, ref mi))
                        {
                            bool tbTop = mi.rcWork.Top > mi.rcMonitor.Top;
                            bool tbLeft = mi.rcWork.Left > mi.rcMonitor.Left;
                            bool tbRight = mi.rcWork.Right < mi.rcMonitor.Right;

                            double dpiScaleY = 1.0;
                            if (SwiftVolumeNativeMethods.GetDpiForMonitor(hMonitor, SwiftVolumeNativeMethods.MDT_EFFECTIVE_DPI, out _, out uint dpiY) == 0)
                            {
                                dpiScaleY = dpiY / 96.0;
                            }
                            int marginPx = (int)Math.Round(10.0 * dpiScaleY);

                            if (!tbTop && !tbLeft && !tbRight)
                            {
                                // 標準タスクバー（下部配置）: ワークエリア下端を超えてタスクバーに埋まらないよう強制補正
                                int maxBottom = mi.rcWork.Bottom - marginPx;
                                if (wp.y + wp.cy > maxBottom && wp.cy > 0)
                                {
                                    wp.y = Math.Max(mi.rcWork.Top + marginPx, maxBottom - wp.cy);
                                    Marshal.StructureToPtr(wp, lParam, true);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return IntPtr.Zero;
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
        _sessionWatchTimer.Stop();
        Interlocked.Exchange(ref _isSessionWatchRunning, 0);

        // 先に非表示にしてから状態クリーンアップ（急縮小による画面下部へのジャンプを根絶）
        this.Opacity = 0;
        this.Hide();

        PurgeInvalidSessions();
        // セッションカードをクリアせず保持し、次回再表示時に前回の高さを維持して差分更新する
        _lastUserAppSliderInteraction = DateTime.MinValue;
        _lastMasterUserInteraction = DateTime.MinValue;
        _lastInputUserInteraction = DateTime.MinValue;
        ExpandedDevicesPanel.Children.Clear();
        _expandedDeviceSliders.Clear();
        _isExpanded = false;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ExpandButton.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush");
        OutputPeakLevel = 0f;
        InputPeakLevel = 0f;
        _smoothedOutputPeak = 0f;
        _smoothedInputPeak = 0f;
        SwiftVolumeSettingsHelper.SaveSettingsImmediately(_settingsAccessor());
    }

    private void PurgeInvalidSessions()
    {
        try
        {
            _cachedSessions.RemoveAll(s => !DeviceEnumerationService.IsSessionAlive(s));
            _sessionMeters.RemoveAll(m => !DeviceEnumerationService.IsSessionAlive(m.Session));

            for (int i = AppSessionsPanel.Children.Count - 1; i >= 0; i--)
            {
                if (AppSessionsPanel.Children[i] is FrameworkElement fe &&
                    fe.Tag is SafeAudioSession session &&
                    !DeviceEnumerationService.IsSessionAlive(session))
                {
                    AppSessionsPanel.Children.RemoveAt(i);
                }
            }
        }
        catch { }
    }

    public void RefreshData()
    {
        RefreshDataAsync();
    }

    private static bool HaveSessionListChanged(List<SafeAudioSession>? a, List<SafeAudioSession>? b)
    {
        if (a == null && b == null) return false;
        if (a == null || b == null) return true;

        var validA = a.Where(s => !DeviceEnumerationService.IsExcludedSession(s.DisplayName, s.DisplayName, s.ProcessId)).ToList();
        var validB = b.Where(s => !DeviceEnumerationService.IsExcludedSession(s.DisplayName, s.DisplayName, s.ProcessId)).ToList();

        if (validA.Count != validB.Count) return true;

        var setA = new HashSet<string>(validA.Select(s => $"{s.ProcessId}_{s.DisplayName}"));
        var setB = new HashSet<string>(validB.Select(s => $"{s.ProcessId}_{s.DisplayName}"));

        return !setA.SetEquals(setB);
    }

    private static bool AreSessionsEquivalent(List<SafeAudioSession>? a, List<SafeAudioSession>? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id ||
                a[i].DisplayName != b[i].DisplayName ||
                Math.Abs(a[i].Volume - b[i].Volume) > 0.015f ||
                a[i].IsMuted != b[i].IsMuted)
            {
                return false;
            }
        }
        return true;
    }

    public async void RefreshDataAsync()
    {
        _isUpdatingUi = true;
        try
        {
            string preDevId = _currentOutputDevice?.Id ?? "";

            // 並列で入出力デバイスを取得しつつ、既に特定済みの出力デバイスのセッション取得も同時に並列開始
            var outDevicesTask = System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeOutputDevices());
            var inDevicesTask = System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeInputDevices());
            var preSessionsTask = !string.IsNullOrEmpty(preDevId)
                ? System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeSessions(preDevId))
                : null;

            await System.Threading.Tasks.Task.WhenAll(outDevicesTask, inDevicesTask);
            var outDevices = outDevicesTask.Result;
            var inDevices = inDevicesTask.Result;

            _outputDevices = outDevices;
            _inputDevices = inDevices;

            int selOutIdx = 0;
            for (int i = 0; i < _outputDevices.Count; i++)
            {
                var d = _outputDevices[i];
                if (d.IsDefault)
                {
                    selOutIdx = i;
                    _currentOutputDevice = d;
                }
            }

            bool outChanged = OutputDeviceCombo.Items.Count != _outputDevices.Count;
            if (!outChanged)
            {
                for (int i = 0; i < _outputDevices.Count; i++)
                {
                    if (!Equals(OutputDeviceCombo.Items[i], _outputDevices[i].Name)) { outChanged = true; break; }
                }
            }
            if (outChanged)
            {
                OutputDeviceCombo.Items.Clear();
                foreach (var d in _outputDevices) OutputDeviceCombo.Items.Add(d.Name);
            }
            if (_outputDevices.Count > 0 && OutputDeviceCombo.SelectedIndex != selOutIdx)
            {
                OutputDeviceCombo.SelectedIndex = selOutIdx;
            }

            int selInIdx = 0;
            for (int i = 0; i < _inputDevices.Count; i++)
            {
                var d = _inputDevices[i];
                if (d.IsDefault)
                {
                    selInIdx = i;
                    _currentInputDevice = d;
                }
            }

            bool inChanged = InputDeviceCombo.Items.Count != _inputDevices.Count;
            if (!inChanged)
            {
                for (int i = 0; i < _inputDevices.Count; i++)
                {
                    if (!Equals(InputDeviceCombo.Items[i], _inputDevices[i].Name)) { inChanged = true; break; }
                }
            }
            if (inChanged)
            {
                InputDeviceCombo.Items.Clear();
                foreach (var d in _inputDevices) InputDeviceCombo.Items.Add(d.Name);
            }
            if (_inputDevices.Count > 0 && InputDeviceCombo.SelectedIndex != selInIdx)
            {
                InputDeviceCombo.SelectedIndex = selInIdx;
            }

            UpdateMasterControls();
            UpdateInputControls();

            // 既定デバイスのセッション取得と展開パネルのセッション取得を並列実行
            if (_currentOutputDevice != null)
            {
                string devId = _currentOutputDevice.Id;
                if (!string.IsNullOrEmpty(preDevId) && preDevId != devId)
                {
                    _meteringService.InvalidateCache();
                    _faultedControls.Clear();
                }

                System.Threading.Tasks.Task<List<SafeAudioSession>> sessionsTask;
                if (preSessionsTask != null && devId == preDevId)
                {
                    sessionsTask = preSessionsTask;
                }
                else
                {
                    sessionsTask = System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeSessions(devId));
                }

                var expandedTask = _isExpanded ? RefreshExpandedDevicesAsync() : System.Threading.Tasks.Task.CompletedTask;

                await System.Threading.Tasks.Task.WhenAll(sessionsTask, expandedTask);

                var sessions = sessionsTask.Result;
                _cachedSessions = sessions;
                UpdateAppSessionsIncremental(sessions);
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
        if (!MasterVolumeSlider.IsMouseCaptureWithin && !MasterVolumeSlider.IsFocused &&
            (DateTime.UtcNow - _lastMasterUserInteraction).TotalMilliseconds >= 800)
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
        // ユーザーがドラッグ・操作中でなければスライダー値をスムーズに更新
        if (!InputVolumeSlider.IsMouseCaptureWithin && !InputVolumeSlider.IsFocused &&
            (DateTime.UtcNow - _lastInputUserInteraction).TotalMilliseconds >= 800)
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
        UpdateMicMonitorButtonState();
    }

    private void RenderAppSessions(List<SafeAudioSession> sessions)
    {
        UpdateAppSessionsIncremental(sessions);
    }

    private void UpdateAppSessionsIncremental(List<SafeAudioSession> sessions)
    {
        if (sessions == null) return;
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

        // 1. 除外対象セッションをフィルタリング（除外アプリの100%復帰はDeviceEnumerationService側で実施済み）
        var validSessions = sessions
            .Where(s => !DeviceEnumerationService.IsExcludedSession(s.DisplayName, s.DisplayName, s.ProcessId))
            .ToList();

        // 2. ユーザーの並び替え順序を適用
        if (settings.AppSortOrder != null && settings.AppSortOrder.Count > 0)
        {
            validSessions = validSessions
                .OrderBy(s =>
                {
                    int idx = settings.AppSortOrder.IndexOf(s.DisplayName);
                    return idx >= 0 ? idx : int.MaxValue;
                })
                .ToList();
        }

        var validDict = validSessions.ToDictionary(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);

        // 3. 終了・消滅したセッションカードの削除 (ドラッグ中でなければ)
        if (!_isDraggingSession)
        {
            for (int i = AppSessionsPanel.Children.Count - 1; i >= 0; i--)
            {
                if (AppSessionsPanel.Children[i] is FrameworkElement fe && fe.Tag is SafeAudioSession oldSess)
                {
                    if (!validDict.ContainsKey(oldSess.DisplayName) || !DeviceEnumerationService.IsSessionAlive(oldSess))
                    {
                        AppSessionsPanel.Children.RemoveAt(i);
                        _appSliders.Remove(oldSess.DisplayName);
                        _sessionMeters.RemoveAll(m => m.Session == oldSess || m.Session.DisplayName == oldSess.DisplayName || (oldSess.ChildSessions != null && oldSess.ChildSessions.Contains(m.Session)));
                    }
                }
            }
        }

        // 4. 既存カードのマップ作成
        var existingCards = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        foreach (UIElement child in AppSessionsPanel.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is SafeAudioSession s)
            {
                existingCards[s.DisplayName] = fe;
            }
        }

        // 5. 既存カードの差分更新 & 新規セッションカードの作成・挿入
        for (int i = 0; i < validSessions.Count; i++)
        {
            var targetSession = validSessions[i];

            if (existingCards.TryGetValue(targetSession.DisplayName, out var card))
            {
                // 既存カードのセッション参照を最新のCOMコントロールに更新
                card.Tag = targetSession;

                // セッションメーターのセッション参照を更新
                foreach (var meter in _sessionMeters)
                {
                    if (meter.Session.DisplayName == targetSession.DisplayName)
                    {
                        meter.Session = targetSession;
                    }
                }

                // ユーザーが現在操作中でなければ、外部からの音量変更（アプリ内音量変更やWindows設定での変更）をUIに同期
                if (_appSliders.TryGetValue(targetSession.DisplayName, out var slider))
                {
                    bool isInteracting = slider.IsMouseCaptureWithin || slider.IsFocused ||
                        (DateTime.UtcNow - _lastUserAppSliderInteraction).TotalMilliseconds < 800;

                    if (!isInteracting)
                    {
                        int newVol = (int)Math.Round(targetSession.Volume * 100f);
                        if (Math.Abs(slider.Value - newVol) >= 1)
                        {
                            _isUpdatingUi = true;
                            try { slider.Value = newVol; }
                            finally { _isUpdatingUi = false; }
                        }
                    }
                }

                // ドラッグ並び替え中でなければ位置をソート順に保つ
                if (!_isDraggingSession)
                {
                    int curIdx = AppSessionsPanel.Children.IndexOf(card);
                    if (curIdx != i && curIdx >= 0)
                    {
                        AppSessionsPanel.Children.RemoveAt(curIdx);
                        int insertIdx = Math.Min(i, AppSessionsPanel.Children.Count);
                        AppSessionsPanel.Children.Insert(insertIdx, card);
                    }
                }
            }
            else
            {
                // 新規セッション: カードを生成して正しい位置に挿入
                var newCard = CreateAppSessionCard(targetSession, currentDevName, toggleSliderStyle);
                existingCards[targetSession.DisplayName] = newCard;
                int insertIdx = Math.Min(i, AppSessionsPanel.Children.Count);
                AppSessionsPanel.Children.Insert(insertIdx, newCard);
            }
        }
    }

    private Border CreateAppSessionCard(SafeAudioSession session, string currentDevName, Style toggleSliderStyle)
    {
        var capturedSession = session;

        var card = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("ControlElevationBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 3),
            Tag = capturedSession
        };

        var mainContainer = new StackPanel();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); // Column 0: ドラッグハンドル
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); // Column 1: アイコン
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Column 2: スライダー

        // ドラッグハンドル（マウスドラッグでアプリ音量行を並び替え）
        var dragHandle = new Border
        {
            Width = 16,
            Height = 26,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeNS,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            ToolTip = SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_Mixer_DragReorder"]
        };
        var handleIcon = new SymbolIcon
        {
            Symbol = SymbolRegular.ReOrderDotsVertical16,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.45
        };
        dragHandle.MouseEnter += (s, e) => handleIcon.Opacity = 0.9;
        dragHandle.MouseLeave += (s, e) => handleIcon.Opacity = 0.45;
        dragHandle.Child = handleIcon;
        Grid.SetColumn(dragHandle, 0);
        grid.Children.Add(dragHandle);

        Action endDrag = () =>
        {
            if (_draggedCard == card)
            {
                card.Opacity = 1.0;
                if (_isDraggingSession)
                {
                    _isDraggingSession = false;
                    SaveAppSortOrder();
                }
                _draggedCard = null;
            }
            if (dragHandle.IsMouseCaptured)
            {
                dragHandle.ReleaseMouseCapture();
            }
        };

        dragHandle.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _draggedCard = card;
            _isDraggingSession = false;
            _dragStartPos = e.GetPosition(AppSessionsPanel);
            dragHandle.CaptureMouse();
            e.Handled = true;
        };

        dragHandle.PreviewMouseMove += (s, e) =>
        {
            if (_draggedCard == card && dragHandle.IsMouseCaptured)
            {
                Point currentPos = e.GetPosition(AppSessionsPanel);
                if (!_isDraggingSession)
                {
                    if (Math.Abs(currentPos.Y - _dragStartPos.Y) >= SystemParameters.MinimumVerticalDragDistance)
                    {
                        _isDraggingSession = true;
                        card.Opacity = 0.55;
                    }
                }

                if (_isDraggingSession)
                {
                    int curIdx = AppSessionsPanel.Children.IndexOf(card);
                    if (curIdx >= 0)
                    {
                        int targetIdx = -1;
                        for (int i = 0; i < AppSessionsPanel.Children.Count; i++)
                        {
                            if (i == curIdx) continue;
                            if (AppSessionsPanel.Children[i] is FrameworkElement otherChild)
                            {
                                var childTopLeft = otherChild.TranslatePoint(new Point(0, 0), AppSessionsPanel);
                                double childMidY = childTopLeft.Y + otherChild.ActualHeight / 2;

                                if (curIdx < i && currentPos.Y > childMidY)
                                {
                                    targetIdx = i;
                                }
                                else if (curIdx > i && currentPos.Y < childMidY)
                                {
                                    targetIdx = i;
                                    break;
                                }
                            }
                        }

                        if (targetIdx >= 0 && targetIdx != curIdx)
                        {
                            AppSessionsPanel.Children.Remove(card);
                            AppSessionsPanel.Children.Insert(targetIdx, card);
                        }
                    }
                    e.Handled = true;
                }
            }
        };

        dragHandle.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (_draggedCard == card)
            {
                endDrag();
                e.Handled = true;
            }
        };

        dragHandle.LostMouseCapture += (s, e) =>
        {
            if (_draggedCard == card)
            {
                endDrag();
            }
        };

        // アイコンコンテナ（アイコン ＋ 複数セッション時の展開バッジを重ねて配置）
        var iconContainer = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };

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
            ToolTip = $"{session.DisplayName} {SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_ClickToMute"]}"
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
                    capturedSession.IsMuted = nextMute;
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

        _appSliders[session.DisplayName] = slider;

        slider.PreviewMouseDown += (s, e) => _lastUserAppSliderInteraction = DateTime.UtcNow;
        slider.PreviewMouseUp += (s, e) => _lastUserAppSliderInteraction = DateTime.UtcNow;
        slider.PreviewMouseMove += (s, e) =>
        {
            if (slider.IsMouseCaptureWithin || e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            {
                _lastUserAppSliderInteraction = DateTime.UtcNow;
            }
        };

        slider.ValueChanged += (s, e) =>
        {
            if (_isUpdatingUi) return;
            _lastUserAppSliderInteraction = DateTime.UtcNow;

            float newVol = (float)(slider.Value / 100.0);
            capturedSession.Volume = newVol;
            if (newVol > 0)
            {
                capturedSession.IsMuted = false;
                iconVisual.Opacity = 1.0;
            }

            // キャッシュ内の該当セッションの音量も同期
            if (_cachedSessions != null)
            {
                foreach (var cs in _cachedSessions)
                {
                    if (cs.DisplayName == capturedSession.DisplayName)
                    {
                        cs.Volume = newVol;
                    }
                }
            }

            // Windows CoreAudio へのダイレクト反映（0ms即時適用）
            // OS（CoreAudio/WASAPI）がPolicyConfigに即座に記憶するため、外部ファイル不要
            var targetControls = capturedSession.Controls.Count > 0 
                ? capturedSession.Controls 
                : (capturedSession.Control != null ? new List<AudioSessionControl> { capturedSession.Control } : new List<AudioSessionControl>());
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
        };
        sliderContainer.Children.Add(slider);

        Grid.SetColumn(sliderContainer, 2);
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
                Margin = new Thickness(0, 0, -1, -1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = string.Format(SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_Mixer_HiddenSessions"], session.ChildSessions.Count - 1)
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
                Margin = new Thickness(44, 4, 0, 2)
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
                childSlider.PreviewMouseDown += (cs, ce) => _lastUserAppSliderInteraction = DateTime.UtcNow;
                childSlider.PreviewMouseUp += (cs, ce) => _lastUserAppSliderInteraction = DateTime.UtcNow;
                childSlider.PreviewMouseMove += (cs, ce) =>
                {
                    if (childSlider.IsMouseCaptureWithin || ce.LeftButton == MouseButtonState.Pressed || ce.RightButton == MouseButtonState.Pressed)
                    {
                        _lastUserAppSliderInteraction = DateTime.UtcNow;
                    }
                };
                childSlider.ValueChanged += (cs, ce) =>
                {
                    if (_isUpdatingUi) return;
                    _lastUserAppSliderInteraction = DateTime.UtcNow;
                    if (capturedChild.Control != null)
                    {
                        try
                        {
                            float cv = (float)(childSlider.Value / 100.0);
                            capturedChild.Volume = cv;
                            capturedChild.Control.SimpleAudioVolume.Volume = cv;
                            if (cv > 0 && capturedChild.Control.SimpleAudioVolume.Mute)
                            {
                                capturedChild.Control.SimpleAudioVolume.Mute = false;
                            }
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

            Grid.SetColumn(iconContainer, 1);
            grid.Children.Add(iconContainer);

            mainContainer.Children.Add(grid);
            mainContainer.Children.Add(childContainer);
        }
        else
        {
            Grid.SetColumn(iconContainer, 1);
            grid.Children.Add(iconContainer);
            mainContainer.Children.Add(grid);
        }

        card.Child = mainContainer;
        return card;
    }

    private void SaveAppSortOrder()
    {
        try
        {
            var order = new List<string>();
            foreach (UIElement child in AppSessionsPanel.Children)
            {
                if (child is FrameworkElement elem && elem.Tag is SafeAudioSession sess)
                {
                    if (!string.IsNullOrEmpty(sess.DisplayName) && !order.Contains(sess.DisplayName))
                    {
                        order.Add(sess.DisplayName);
                    }
                }
            }

            var settings = _settingsAccessor();
            settings.AppSortOrder = order;
            SwiftVolumeSettingsHelper.SaveSettingsDebounced(settings);

            if (_cachedSessions != null && _cachedSessions.Count > 0)
            {
                _cachedSessions = _cachedSessions
                    .OrderBy(s =>
                    {
                        int idx = order.IndexOf(s.DisplayName);
                        return idx >= 0 ? idx : int.MaxValue;
                    })
                    .ToList();
            }
        }
        catch { }
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
            if (_preExpandLeft.HasValue)
            {
                double currentRight = _preExpandLeft.Value + 370;
                double newLeft = Math.Max(SystemParameters.WorkArea.Left + 8, currentRight - targetW);
                this.Left = newLeft;
            }
            else
            {
                UpdateWindowPosition();
            }
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
                    Text = SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_DefaultDevice"],
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White
                };
                titleSp.Children.Add(defaultBadge);
            }

            headerGrid.Children.Add(titleSp);

            var setDefaultBtn = new Button { Content = SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_SetAsDefault"], Appearance = ControlAppearance.Secondary, FontSize = 11, Padding = new Thickness(6, 2, 6, 2) };
            if (dev.IsDefault) setDefaultBtn.Visibility = Visibility.Collapsed;
            string capturedId = dev.Id;
            setDefaultBtn.Click += (s, e) =>
            {
                AudioDeviceHelper.PreApplyDeviceVolume(capturedId);
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    PolicyConfig.SetDefaultDevice(capturedId);
                    Dispatcher.InvokeAsync(RefreshData);
                });
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

            volSlider.PreviewMouseDown += (s, e) => _lastMasterUserInteraction = DateTime.UtcNow;
            volSlider.PreviewMouseUp += (s, e) => _lastMasterUserInteraction = DateTime.UtcNow;
            volSlider.PreviewMouseMove += (s, e) =>
            {
                if (volSlider.IsMouseCaptureWithin || e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
                {
                    _lastMasterUserInteraction = DateTime.UtcNow;
                }
            };

            float lastVolChangeVal = -1f;
            volSlider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _lastMasterUserInteraction = DateTime.UtcNow;
                float v = (float)volSlider.Value;
                if (Math.Abs(lastVolChangeVal - v) < 0.25f && v > 0 && v < 100) return;
                lastVolChangeVal = v;

                dev.Volume = v / 100f;

                // 本当に連動している裏デバイスのみ判定 (例: FxSoundの出力先裏デバイス1)
                bool isLinked = IsDeviceLinkedWithDefault(dev);

                if (isLinked)
                {
                    _singleLinkedDeviceId = targetDevId;
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
            };
            sp.Children.Add(volSlider);

            var appsTitle = new TextBlock { Text = SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_AppVolumeSection"], FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 4, 0, 6) };
            sp.Children.Add(appsTitle);

            List<SafeAudioSession> devSessions = new();
            if (sessionTasks.TryGetValue(dev.Id, out var sTask) && sTask.IsCompletedSuccessfully)
            {
                devSessions = sTask.Result ?? new List<SafeAudioSession>();
            }
            if (devSessions.Count == 0)
            {
                var noApp = new TextBlock { Text = SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_NoActiveApps"], FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush"), Margin = new Thickness(4, 4, 4, 4) };
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
                        ToolTip = $"{s.DisplayName} {SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_ClickToMute"]}"
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
                    sSlider.PreviewMouseDown += (sSender, sE) => _lastUserAppSliderInteraction = DateTime.UtcNow;
                    sSlider.PreviewMouseUp += (sSender, sE) => _lastUserAppSliderInteraction = DateTime.UtcNow;
                    sSlider.PreviewMouseMove += (sSender, sE) =>
                    {
                        if (sSlider.IsMouseCaptureWithin || sE.LeftButton == MouseButtonState.Pressed || sE.RightButton == MouseButtonState.Pressed)
                        {
                            _lastUserAppSliderInteraction = DateTime.UtcNow;
                        }
                    };
                    sSlider.ValueChanged += (sSender, sE) =>
                    {
                        if (_isUpdatingUi) return;
                        _lastUserAppSliderInteraction = DateTime.UtcNow;
                        float newVol = (float)(sSlider.Value / 100.0);
                        capturedDevSession.Volume = newVol;
                        if (newVol > 0)
                        {
                            capturedDevSession.IsMuted = false;
                            appIconVisual.Opacity = 1.0;
                        }

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

        if (_isExpanded)
        {
            if (!_preExpandLeft.HasValue)
            {
                _preExpandLeft = this.Left;
            }

            double otherDevsCount = Math.Max(0, _outputDevices.Count - 1);
            double targetW = Math.Min(370 + (otherDevsCount * 280), SystemParameters.WorkArea.Width - 24);
            this.Width = targetW;

            // 右端位置を保持して左側に展開
            double currentRight = _preExpandLeft.Value + 370;
            double newLeft = currentRight - targetW;
            double workLeft = SystemParameters.WorkArea.Left + 8;
            if (newLeft < workLeft) newLeft = workLeft;
            this.Left = newLeft;

            _ = RefreshExpandedDevicesAsync();
        }
        else
        {
            this.Width = 370;
            if (_preExpandLeft.HasValue)
            {
                this.Left = _preExpandLeft.Value;
            }
            else
            {
                UpdateWindowPosition();
            }
        }
    }

    private float _lastMasterChangedVol = -1f;

    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || _currentOutputDevice == null) return;
        _lastMasterUserInteraction = DateTime.UtcNow;
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
        _lastInputUserInteraction = DateTime.UtcNow;
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
        MicMuteButton.Foreground = (System.Windows.Media.Brush)FindResource(isMuted ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
        SwiftVolumeModule.Instance?.NotifyMicMuteChanged(isMuted);
    }

    private void OnMicMonitorClicked(object sender, RoutedEventArgs e)
    {
        var settings = _settingsAccessor();
        settings.EnableMicMonitoring = !settings.EnableMicMonitoring;
        SwiftVolumeSettingsHelper.SaveSettingsImmediately(settings);

        if (settings.EnableMicMonitoring)
        {
            SwiftVolumeModule.Instance?.MonitorEngine?.Start();
        }
        else
        {
            SwiftVolumeModule.Instance?.MonitorEngine?.Stop();
        }
        UpdateMicMonitorButtonState();
    }

    private void UpdateMicMonitorButtonState()
    {
        var settings = _settingsAccessor();
        bool isMonitoring = settings.EnableMicMonitoring;
        MicMonitorButton.Foreground = (System.Windows.Media.Brush)FindResource(
            isMonitoring ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        if (MicNoiseGateMenuItem != null)
        {
            MicNoiseGateMenuItem.IsChecked = settings.EnableMicNoiseGate;
        }
    }

    private void OnMicNoiseGateMenuClicked(object sender, RoutedEventArgs e)
    {
        var settings = _settingsAccessor();
        settings.EnableMicNoiseGate = MicNoiseGateMenuItem.IsChecked;
        SwiftVolumeSettingsHelper.SaveSettingsImmediately(settings);
        SwiftVolumeModule.Instance?.MonitorEngine?.UpdateNoiseGate(settings.EnableMicNoiseGate, settings.MicNoiseGateThresholdDb);
    }

    private void OnOpenMicMonitoringSettingsClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        SwiftVolumeModule.Instance?.OpenSettings();
    }

    private async void OnOutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        int idx = OutputDeviceCombo.SelectedIndex;
        if (idx >= 0 && idx < _outputDevices.Count)
        {
            var target = _outputDevices[idx];
            if (_currentOutputDevice != null && _currentOutputDevice.Id == target.Id) return;
            _isUpdatingUi = true;
            try
            {
                _currentOutputDevice = target;

                // 1. 直ちに切り替え中HUD表示 ＆ 音量フェードアウト
                SwiftVolumeModule.Instance?.NotifyDeviceSwitching(target.Name, false);
                await AudioDeviceHelper.FadeOutMasterVolumeAsync(durationMs: 70);

                // 2. バックグラウンドでデバイス切り替え完了を実行
                string devId = target.Id;
                AudioDeviceHelper.PreApplyDeviceVolume(devId);
                await System.Threading.Tasks.Task.Run(() => PolicyConfig.SetDefaultDevice(devId));

                // 3. 切り替え先音量へスムーズにフェードイン適用
                float targetVol = target.Volume * 100f;
                var settings = _settingsAccessor();
                string effKey = AudioDeviceHelper.GetEffectiveDeviceVolumeKey(target.Name);
                if (settings.DeviceMasterVolumes.TryGetValue(effKey, out float savedVol) ||
                    settings.DeviceMasterVolumes.TryGetValue(target.Name, out savedVol))
                {
                    targetVol = savedVol * 100f;
                }

                await AudioDeviceHelper.FadeInMasterVolumeAsync(targetVol, durationMs: 120);
                UpdateMasterControls();

                // 4. メータリングキャッシュのクリア & 破損セッションコントロールの除外リスト初期化
                _meteringService.InvalidateCache();
                _faultedControls.Clear();

                // 5. セッション取得・反映（二段階リフレッシュ：100ms初期反映 & 350ms追従反映）
                await System.Threading.Tasks.Task.Delay(100);
                var sessions = await System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeSessions(devId));
                AppSessionsPanel.Children.Clear();
                _sessionMeters.Clear();
                _appSliders.Clear();
                _cachedSessions = sessions;
                UpdateAppSessionsIncremental(sessions);
                if (_isExpanded) await RefreshExpandedDevicesAsync();

                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(250);
                    var freshSessions = _deviceService.GetSafeSessions(devId);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!this.IsVisible || _currentOutputDevice?.Id != devId) return;
                        _faultedControls.Clear();
                        _cachedSessions = freshSessions;
                        UpdateAppSessionsIncremental(freshSessions);
                    });
                });
            }
            catch { }
            finally
            {
                _isUpdatingUi = false;
            }
        }
    }

    private async void OnInputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        int idx = InputDeviceCombo.SelectedIndex;
        if (idx >= 0 && idx < _inputDevices.Count)
        {
            var target = _inputDevices[idx];
            if (_currentInputDevice != null && _currentInputDevice.Id == target.Id) return;
            _isUpdatingUi = true;
            try
            {
                _currentInputDevice = target;
                SwiftVolumeModule.Instance?.NotifyDeviceSwitching(target.Name, true);
                UpdateInputControls();

                string devId = target.Id;
                await System.Threading.Tasks.Task.Run(() => PolicyConfig.SetDefaultDevice(devId));
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
