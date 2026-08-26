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
            if (this.IsVisible)
            {
                Dispatcher.InvokeAsync(() => RefreshDataAsync());
            }
        };

        AudioDeviceHelper.MasterVolumeChanged += (vol, muted) =>
        {
            if (this.IsVisible && !_isUpdatingUi)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (_currentOutputDevice != null && _currentOutputDevice.IsDefault)
                    {
                        _currentOutputDevice.Volume = vol / 100f;
                        _currentOutputDevice.IsMuted = muted;
                        UpdateMasterControls();
                    }
                });
            }
        };
    }

    public void ShowAtCursorOrTray()
    {
        _lastShowTime = DateTime.Now;

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

        // バックグラウンドで非同期にデバイス＆セッション情報を更新
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
            var outDevices = await System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeOutputDevices());
            var inDevices = await System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeInputDevices());

            _outputDevices = outDevices;
            _inputDevices = inDevices;

            OutputDeviceCombo.Items.Clear();
            int selOutIdx = 0;
            for (int i = 0; i < _outputDevices.Count; i++)
            {
                var d = _outputDevices[i];
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

            if (_currentOutputDevice != null)
            {
                string devId = _currentOutputDevice.Id;
                var sessions = await System.Threading.Tasks.Task.Run(() => _deviceService.GetSafeSessions(devId));
                RenderAppSessions(sessions);
            }

            if (_isExpanded) await RefreshExpandedDevicesAsync();
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
        var settings = _settingsAccessor();
        if (settings.DeviceMasterVolumes.TryGetValue(_currentOutputDevice.Name, out float savedDevVol))
        {
            if (Math.Abs(_currentOutputDevice.Volume - savedDevVol) > 0.01f)
            {
                _currentOutputDevice.Volume = savedDevVol;
                AudioDeviceHelper.SetMasterVolume(savedDevVol * 100f);
            }
        }
        else
        {
            settings.DeviceMasterVolumes[_currentOutputDevice.Name] = _currentOutputDevice.Volume;
        }

        int vol = (int)Math.Round(_currentOutputDevice.Volume * 100f);
        // ユーザーがドラッグ・操作中は外部イベントによる値の巻き戻し（跳ね戻り）を防止
        if (!MasterVolumeSlider.IsMouseCaptureWithin && !MasterVolumeSlider.IsFocused)
        {
            MasterVolumeSlider.Value = vol;
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
            InputVolumeSlider.Value = vol;
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
        string currentDevName = _currentOutputDevice?.Name ?? "Default";

        foreach (var session in sessions)
        {
            // 保存済み音量の復元 (同アプリでもデバイスごとに記憶)
            string volKey = $"{currentDevName}_{session.DisplayName}";
            if (settings.AppVolumes.TryGetValue(volKey, out float savedVol))
            {
                session.Volume = savedVol;
                var ctrls = session.Controls.Count > 0 ? session.Controls : (session.Control != null ? new List<AudioSessionControl> { session.Control } : new List<AudioSessionControl>());
                foreach (var ctrl in ctrls)
                {
                    try { ctrl.SimpleAudioVolume.Volume = savedVol; } catch { }
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
                var targetControls = capturedSession.Controls.Count > 0 ? capturedSession.Controls : (capturedSession.Control != null ? new List<AudioSessionControl> { capturedSession.Control } : new List<AudioSessionControl>());
                if (targetControls.Count > 0)
                {
                    try 
                    { 
                        float newVol = (float)(slider.Value / 100.0);
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
                        if (newVol > 0) iconVisual.Opacity = 1.0;

                        // デバイス別に確実に音量を保存
                        var curSettings = _settingsAccessor();
                        string devKey = $"{currentDevName}_{capturedSession.DisplayName}";
                        curSettings.AppVolumes[devKey] = newVol;

                        // FxSoundなどの仮想/連動オーディオデバイスの場合、他デバイスとも連動
                        if (currentDevName.Contains("FxSound", StringComparison.OrdinalIgnoreCase) || 
                            currentDevName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var outDev in _outputDevices)
                            {
                                curSettings.AppVolumes[$"{outDev.Name}_{capturedSession.DisplayName}"] = newVol;
                            }
                        }
                    } 
                    catch { }
                }
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
                PolicyConfig.SetDefaultDevice(capturedId);
                RefreshData();
            };
            Grid.SetColumn(setDefaultBtn, 1);
            headerGrid.Children.Add(setDefaultBtn);
            sp.Children.Add(headerGrid);

            var expDevSettings = _settingsAccessor();
            float initialDevVol = dev.Volume;
            if (expDevSettings.DeviceMasterVolumes.TryGetValue(dev.Name, out float savedDevVol))
            {
                initialDevVol = savedDevVol;
                _deviceService.SetDeviceVolume(dev.Id, savedDevVol * 100f);
            }

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
            volSlider.ValueChanged += (s, e) =>
            {
                float v = (float)volSlider.Value;
                _deviceService.SetDeviceVolume(targetDevId, v);
                var curSettings = _settingsAccessor();
                curSettings.DeviceMasterVolumes[targetDevName] = v / 100f;
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
                                appIconVisual.Opacity = next ? 0.35 : 1.0;
                            }
                            catch { }
                        }
                    };
                    Grid.SetColumn(appIconBtn, 0);
                    appGrid.Children.Add(appIconBtn);

                    // 拡張パネルでも保存済み音量を復元
                    var expSettings = _settingsAccessor();
                    string expDevKey = $"{dev.Name}_{s.DisplayName}";
                    if (expSettings.AppVolumes.TryGetValue(expDevKey, out float expSavedVol))
                    {
                        s.Volume = expSavedVol;
                        var ctrls = s.Controls.Count > 0 ? s.Controls : (s.Control != null ? new List<AudioSessionControl> { s.Control } : new List<AudioSessionControl>());
                        foreach (var ctrl in ctrls)
                        {
                            try { ctrl.SimpleAudioVolume.Volume = expSavedVol; } catch { }
                        }
                    }

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
                        var targetControls = capturedDevSession.Controls.Count > 0 ? capturedDevSession.Controls : (capturedDevSession.Control != null ? new List<AudioSessionControl> { capturedDevSession.Control } : new List<AudioSessionControl>());
                        if (targetControls.Count > 0)
                        {
                            try 
                            { 
                                float newVol = (float)(sSlider.Value / 100.0);
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
                                if (newVol > 0) appIconVisual.Opacity = 1.0;

                                var curSettings = _settingsAccessor();
                                string devVolKey = $"{dev.Name}_{capturedDevSession.DisplayName}";
                                curSettings.AppVolumes[devVolKey] = newVol;
                            } 
                            catch { }
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
        ShowAtCursorOrTray();
    }

    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || _currentOutputDevice == null) return;
        float vol = (float)MasterVolumeSlider.Value;
        _currentOutputDevice.Volume = vol / 100f;
        AudioDeviceHelper.SetMasterVolume(vol);
        var settings = _settingsAccessor();
        settings.DeviceMasterVolumes[_currentOutputDevice.Name] = vol / 100f;
        MasterMuteButton.Icon = new SymbolIcon(vol == 0 ? SymbolRegular.SpeakerOff24 : SymbolRegular.Speaker224);
    }

    private void OnMasterMuteClicked(object sender, RoutedEventArgs e)
    {
        var (_, isMuted) = AudioDeviceHelper.ToggleMute();
        MasterMuteButton.Icon = new SymbolIcon(isMuted ? SymbolRegular.SpeakerOff24 : SymbolRegular.Speaker224);
    }

    private void OnInputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || _currentInputDevice == null) return;
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
                PolicyConfig.SetDefaultDevice(target.Id);
                _currentOutputDevice = target;
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
