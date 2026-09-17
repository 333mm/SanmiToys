using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SanmiToys.Modules.SwiftVolume.Core;
using Wpf.Ui.Controls;

namespace SanmiToys.Modules.SwiftVolume.Views;

public partial class VolumeHudWindow : Window
{
    private readonly DispatcherTimer _hideTimer;
    private readonly ScaleTransform _scaleTransform = new(1.0, 1.0);
    private int _animationGeneration = 0;
    private bool _isFadingOut = false;
    private static readonly SolidColorBrush ModernGreenBrush = CreateFrozenBrush("#10B981");
    private static readonly SolidColorBrush ModernRedBrush = CreateFrozenBrush("#EF4444");

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public VolumeHudWindow()
    {
        InitializeComponent();

        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = SanmiToys.Core.Helpers.NativeMethods.GetWindowLong(hwnd, SanmiToys.Core.Helpers.NativeMethods.GWL_EXSTYLE);
            SanmiToys.Core.Helpers.NativeMethods.SetWindowLong(hwnd, SanmiToys.Core.Helpers.NativeMethods.GWL_EXSTYLE,
                exStyle | SanmiToys.Core.Helpers.NativeMethods.WS_EX_TOOLWINDOW | SanmiToys.Core.Helpers.NativeMethods.WS_EX_NOACTIVATE);
        }

        MainCard.LayoutTransform = _scaleTransform;

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (s, e) =>
        {
            _hideTimer.Stop();
            FadeOut();
        };
    }

    private void ApplyScale(int hudSize)
    {
        double scale = hudSize switch
        {
            0 => 0.82,  // 小
            2 => 1.25,  // 大
            _ => 1.0    // 中 (標準)
        };
        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;
    }

    private void ResetAnimationAndShow()
    {
        _hideTimer.Stop();
        _isFadingOut = false;
        _animationGeneration++;

        this.BeginAnimation(UIElement.OpacityProperty, null);
        this.Opacity = 1.0;
        this.Visibility = Visibility.Visible;
    }

    public void ShowVolume(float volumePercent, bool isMuted, double durationSeconds = 1.2, int position = 0, int hudSize = 1)
    {
        ResetAnimationAndShow();

        ApplyScale(hudSize);

        DeviceModeGrid.Visibility = Visibility.Collapsed;
        MicMuteModeGrid.Visibility = Visibility.Collapsed;
        if (DeviceSwitchingSpinner != null) DeviceSwitchingSpinner.Visibility = Visibility.Collapsed;

        if (isMuted)
        {
            // スピーカーミュート時はマイクミュートと同様のシンプルスタイル（赤色アイコン + テキスト）
            VolumeModeGrid.Visibility = Visibility.Collapsed;
            SpeakerMuteModeGrid.Visibility = Visibility.Visible;

            var loc = SanmiToys.Core.Services.LocalizationService.Instance;
            SpeakerMuteIcon.Symbol = SymbolRegular.SpeakerOff24;
            SpeakerMuteIcon.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            SpeakerMuteStatusText.Text = loc["SwiftVolume_Hud_SpeakerMuted"];
            SpeakerMuteSubText.Text = loc["SwiftVolume_Tray_Speaker"];
        }
        else
        {
            // 解除時は今のスタイルのまま（プログレスバー + 音量数値）
            VolumeModeGrid.Visibility = Visibility.Visible;
            SpeakerMuteModeGrid.Visibility = Visibility.Collapsed;

            VolumeProgress.Value = volumePercent;
            VolumeLabel.Text = $"{(int)volumePercent}";

            SpeakerIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");

            if (volumePercent <= 0)
            {
                SpeakerIcon.Symbol = SymbolRegular.SpeakerOff24;
            }
            else if (volumePercent < 33)
            {
                SpeakerIcon.Symbol = SymbolRegular.Speaker024;
            }
            else if (volumePercent < 66)
            {
                SpeakerIcon.Symbol = SymbolRegular.Speaker124;
            }
            else
            {
                SpeakerIcon.Symbol = SymbolRegular.Speaker224;
            }
        }

        UpdateLayout();
        PositionWindow(position);

        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.5, durationSeconds));
        _hideTimer.Start();
    }

    public void ShowDeviceSwitch(string deviceName, bool isInput = false, double durationSeconds = 1.5, int position = 0, int hudSize = 1)
    {
        ResetAnimationAndShow();

        ApplyScale(hudSize);

        VolumeModeGrid.Visibility = Visibility.Collapsed;
        SpeakerMuteModeGrid.Visibility = Visibility.Collapsed;
        DeviceModeGrid.Visibility = Visibility.Visible;
        MicMuteModeGrid.Visibility = Visibility.Collapsed;
        if (DeviceSwitchingSpinner != null) DeviceSwitchingSpinner.Visibility = Visibility.Collapsed;

        DeviceIcon.Symbol = isInput ? SymbolRegular.Mic24 : SymbolRegular.Speaker224;
        DeviceTitleText.Text = isInput 
            ? SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_Hud_MicSwitch"] 
            : SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_Hud_SpeakerSwitch"];
        DeviceNameText.Text = !string.IsNullOrWhiteSpace(deviceName) ? deviceName : (isInput ? "Mic" : "Speaker");

        UpdateLayout();
        PositionWindow(position);

        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.5, durationSeconds));
        _hideTimer.Start();
    }

    public void ShowDeviceSwitching(string deviceName, bool isInput = false, int position = 0, int hudSize = 1)
    {
        ResetAnimationAndShow();

        ApplyScale(hudSize);

        VolumeModeGrid.Visibility = Visibility.Collapsed;
        SpeakerMuteModeGrid.Visibility = Visibility.Collapsed;
        DeviceModeGrid.Visibility = Visibility.Visible;
        MicMuteModeGrid.Visibility = Visibility.Collapsed;

        DeviceIcon.Symbol = isInput ? SymbolRegular.Mic24 : SymbolRegular.Speaker224;
        DeviceTitleText.Text = isInput 
            ? SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_Hud_MicSwitching"] 
            : SanmiToys.Core.Services.LocalizationService.Instance["SwiftVolume_Hud_SpeakerSwitching"];
        DeviceNameText.Text = !string.IsNullOrWhiteSpace(deviceName) ? deviceName : (isInput ? "Mic" : "Speaker");

        if (DeviceSwitchingSpinner != null)
        {
            DeviceSwitchingSpinner.Visibility = Visibility.Visible;
        }

        UpdateLayout();
        PositionWindow(position);

        // 完了を待つ間はタイマーで非表示にしない
        _hideTimer.Stop();
    }

    public void ShowMicMute(bool isMuted, double durationSeconds = 1.2, int position = 0, int hudSize = 1)
    {
        ResetAnimationAndShow();

        ApplyScale(hudSize);

        VolumeModeGrid.Visibility = Visibility.Collapsed;
        SpeakerMuteModeGrid.Visibility = Visibility.Collapsed;
        DeviceModeGrid.Visibility = Visibility.Collapsed;
        MicMuteModeGrid.Visibility = Visibility.Visible;
        if (DeviceSwitchingSpinner != null) DeviceSwitchingSpinner.Visibility = Visibility.Collapsed;

        var loc = SanmiToys.Core.Services.LocalizationService.Instance;
        MicMuteSubText.Text = loc["SwiftVolume_Tray_Mic"];
        if (isMuted)
        {
            MicMuteIcon.Symbol = SymbolRegular.MicOff24;
            MicMuteIcon.Foreground = ModernRedBrush;
            MicMuteStatusText.Text = loc["SwiftVolume_Hud_MicMuted"];
        }
        else
        {
            MicMuteIcon.Symbol = SymbolRegular.Mic24;
            MicMuteIcon.Foreground = ModernGreenBrush;
            MicMuteStatusText.Text = loc["SwiftVolume_Hud_MicUnmuted"];
        }

        UpdateLayout();
        PositionWindow(position);

        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.5, durationSeconds));
        _hideTimer.Start();
    }

    private void PositionWindow(int position)
    {
        try
        {
            SwiftVolumeNativeMethods.GetCursorPos(out var p);

            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;

            SwiftVolumeNativeMethods.RECT rcWork = new()
            {
                Left = 0, Top = 0,
                Right = (int)SystemParameters.WorkArea.Width,
                Bottom = (int)SystemParameters.WorkArea.Height
            };

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
                    rcWork = mi.rcWork;
                }
            }

            double workLeft = rcWork.Left / dpiScaleX;
            double workTop = rcWork.Top / dpiScaleY;
            double workWidth = (rcWork.Right - rcWork.Left) / dpiScaleX;
            double workHeight = (rcWork.Bottom - rcWork.Top) / dpiScaleY;
            double workRight = workLeft + workWidth;
            double workBottom = workTop + workHeight;

            // 決定論的コンテンツサイズ計測（初回・2回目以降の表示位置のズレを根絶）
            MainCard.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var cardDesired = MainCard.DesiredSize;
            double w = Math.Max(this.MinWidth, cardDesired.Width + MainCard.Margin.Left + MainCard.Margin.Right);
            double h = Math.Max(this.MinHeight, cardDesired.Height + MainCard.Margin.Top + MainCard.Margin.Bottom);

            double margin = 32.0;
            double left, top;

            switch (position)
            {
                case 1: // 上部中央
                    left = workLeft + (workWidth - w) / 2.0;
                    top = workTop + margin;
                    break;
                case 2: // 下部中央
                    left = workLeft + (workWidth - w) / 2.0;
                    top = workBottom - h - margin;
                    break;
                case 3: // 左上
                    left = workLeft + margin;
                    top = workTop + margin;
                    break;
                case 4: // 右上
                    left = workRight - w - margin;
                    top = workTop + margin;
                    break;
                case 5: // 左下
                    left = workLeft + margin;
                    top = workBottom - h - margin;
                    break;
                case 6: // 右下
                    left = workRight - w - margin;
                    top = workBottom - h - margin;
                    break;
                default: // 0: 画面中央
                    left = workLeft + (workWidth - w) / 2.0;
                    top = workTop + (workHeight - h) / 2.0;
                    break;
            }

            // クランプ
            if (left < workLeft + 8) left = workLeft + 8;
            if (left + w > workRight - 8) left = workRight - w - 8;
            if (top < workTop + 8) top = workTop + 8;
            if (top + h > workBottom - 8) top = workBottom - h - 8;

            this.Left = left;
            this.Top = top;
        }
        catch
        {
            this.Left = (SystemParameters.PrimaryScreenWidth - 280) / 2.0;
            this.Top = SystemParameters.PrimaryScreenHeight - 120;
        }
    }

    private void FadeOut()
    {
        _isFadingOut = true;
        int currentGen = ++_animationGeneration;

        var anim = new DoubleAnimation
        {
            From = this.Opacity,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (s, e) =>
        {
            if (_animationGeneration == currentGen && _isFadingOut)
            {
                _isFadingOut = false;
                this.Opacity = 0.0;
                this.Visibility = Visibility.Hidden;
            }
        };
        this.BeginAnimation(UIElement.OpacityProperty, anim);
    }
}
