using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace SanmiToys.Modules.FocusDimmer.Views;

public partial class ModernColorPickerWindow : Window
{
    private double _hue = 0;        // 0..360
    private double _sat = 1.0;      // 0..1
    private double _val = 0.0;      // 0..1
    private bool _isUpdating = false;
    private bool _isDraggingSatVal = false;

    public string SelectedColorHex { get; private set; } = "#000000";

    public ModernColorPickerWindow(string initialHex = "#000000")
    {
        InitializeComponent();
        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);
        SetColorFromHex(initialHex);
    }

    private void SetColorFromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) hex = "#000000";
        hex = hex.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            RgbToHsv(color.R, color.G, color.B, out _hue, out _sat, out _val);

            _isUpdating = true;
            HueSlider.Value = _hue;
            HexInputBox.Text = hex.ToUpper();
            UpdateUi();
            _isUpdating = false;
        }
        catch
        {
            _hue = 0;
            _sat = 0;
            _val = 0;
            UpdateUi();
        }
    }

    private void UpdateUi()
    {
        // 1. Base Hue Color for 2D Box
        var hueColor = HsvToRgb(_hue, 1.0, 1.0);
        HueBaseRect.Fill = new SolidColorBrush(hueColor);

        // 2. Final Color
        var finalColor = HsvToRgb(_hue, _sat, _val);
        SelectedColorHex = $"#{finalColor.R:X2}{finalColor.G:X2}{finalColor.B:X2}";
        ColorPreviewRect.Background = new SolidColorBrush(finalColor);

        if (!_isUpdating)
        {
            _isUpdating = true;
            HexInputBox.Text = SelectedColorHex;
            _isUpdating = false;
        }

        // 3. Handle Position in SatValGrid
        double w = SatValGrid.ActualWidth > 0 ? SatValGrid.ActualWidth : 308;
        double h = SatValGrid.ActualHeight > 0 ? SatValGrid.ActualHeight : 180;

        double handleX = _sat * w;
        double handleY = (1.0 - _val) * h;

        Canvas.SetLeft(SatValHandle, handleX);
        Canvas.SetTop(SatValHandle, handleY);
    }

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating) return;
        _hue = HueSlider.Value;
        UpdateUi();
    }

    private void OnSatValMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSatVal = true;
        SatValGrid.CaptureMouse();
        UpdateSatValFromMouse(e.GetPosition(SatValGrid));
    }

    private void OnSatValMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingSatVal)
        {
            UpdateSatValFromMouse(e.GetPosition(SatValGrid));
        }
    }

    private void OnSatValMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingSatVal)
        {
            _isDraggingSatVal = false;
            SatValGrid.ReleaseMouseCapture();
        }
    }

    private void UpdateSatValFromMouse(Point p)
    {
        double w = SatValGrid.ActualWidth;
        double h = SatValGrid.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double x = Math.Clamp(p.X, 0, w);
        double y = Math.Clamp(p.Y, 0, h);

        _sat = x / w;
        _val = 1.0 - (y / h);

        UpdateUi();
    }

    private void OnHexInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        string text = HexInputBox.Text.Trim();
        if (text.Length == 7 && text.StartsWith('#'))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(text);
                RgbToHsv(color.R, color.G, color.B, out _hue, out _sat, out _val);

                _isUpdating = true;
                HueSlider.Value = _hue;
                UpdateUi();
                _isUpdating = false;
            }
            catch { }
        }
    }

    private void OnPresetClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string hex)
        {
            SetColorFromHex(hex);
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
        this.Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        this.Close();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateUi();
    }

    #region HSV / RGB Conversion Helpers
    private static Color HsvToRgb(double h, double s, double v)
    {
        int hi = (int)(Math.Floor(h / 60.0)) % 6;
        double f = (h / 60.0) - Math.Floor(h / 60.0);

        v = v * 255.0;
        byte vVal = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
        byte p = (byte)Math.Clamp((int)Math.Round(v * (1.0 - s)), 0, 255);
        byte q = (byte)Math.Clamp((int)Math.Round(v * (1.0 - (f * s))), 0, 255);
        byte t = (byte)Math.Clamp((int)Math.Round(v * (1.0 - ((1.0 - f) * s))), 0, 255);

        return hi switch
        {
            0 => Color.FromRgb(vVal, t, p),
            1 => Color.FromRgb(q, vVal, p),
            2 => Color.FromRgb(p, vVal, t),
            3 => Color.FromRgb(p, q, vVal),
            4 => Color.FromRgb(t, p, vVal),
            _ => Color.FromRgb(vVal, p, q)
        };
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0)
        {
            h = 0;
        }
        else if (max == rd)
        {
            h = 60.0 * (((gd - bd) / delta) % 6.0);
            if (h < 0) h += 360.0;
        }
        else if (max == gd)
        {
            h = 60.0 * (((bd - rd) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((rd - gd) / delta) + 4.0);
        }
    }
    #endregion
}
