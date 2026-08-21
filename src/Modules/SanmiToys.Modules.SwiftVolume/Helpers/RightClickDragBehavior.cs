using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace SanmiToys.Modules.SwiftVolume.Helpers;

public static class RightClickDragBehavior
{
    public static readonly DependencyProperty EnableRightClickDragProperty =
        DependencyProperty.RegisterAttached(
            "EnableRightClickDrag",
            typeof(bool),
            typeof(RightClickDragBehavior),
            new PropertyMetadata(false, OnEnableRightClickDragChanged));

    public static bool GetEnableRightClickDrag(DependencyObject obj)
    {
        return (bool)obj.GetValue(EnableRightClickDragProperty);
    }

    public static void SetEnableRightClickDrag(DependencyObject obj, bool value)
    {
        obj.SetValue(EnableRightClickDragProperty, value);
    }

    private static void OnEnableRightClickDragChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Slider slider)
        {
            if ((bool)e.NewValue)
            {
                slider.PreviewMouseRightButtonDown += Slider_PreviewMouseRightButtonDown;
                slider.PreviewMouseMove += Slider_PreviewMouseMove;
                slider.PreviewMouseRightButtonUp += Slider_PreviewMouseRightButtonUp;
            }
            else
            {
                slider.PreviewMouseRightButtonDown -= Slider_PreviewMouseRightButtonDown;
                slider.PreviewMouseMove -= Slider_PreviewMouseMove;
                slider.PreviewMouseRightButtonUp -= Slider_PreviewMouseRightButtonUp;
            }
        }
    }

    private static bool _isRightDragging = false;

    private static void Slider_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            _isRightDragging = true;
            slider.CaptureMouse();
            UpdateValueToMousePosition(slider, e.GetPosition(slider));
            e.Handled = true;
        }
    }

    private static void Slider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isRightDragging && sender is Slider slider && slider.IsMouseCaptured)
        {
            UpdateValueToMousePosition(slider, e.GetPosition(slider));
            e.Handled = true;
        }
    }

    private static void Slider_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRightDragging && sender is Slider slider)
        {
            _isRightDragging = false;
            slider.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static void UpdateValueToMousePosition(Slider slider, Point mousePosition)
    {
        double width = slider.ActualWidth;
        double height = slider.ActualHeight;
        if (width <= 0 || height <= 0) return;

        double percent;
        if (slider.Orientation == Orientation.Vertical)
        {
            double margin = 11.0;
            double trackHeight = height - (margin * 2);
            if (trackHeight <= 0) return;

            double mouseY = mousePosition.Y - margin;
            percent = 1.0 - (mouseY / trackHeight);
        }
        else
        {
            double margin = 21.0;
            double trackWidth = width - (margin * 2);
            if (trackWidth <= 0) return;

            double mouseX = mousePosition.X - margin;
            percent = mouseX / trackWidth;
        }

        percent = Math.Clamp(percent, 0.0, 1.0);
        double range = slider.Maximum - slider.Minimum;
        double rawValue = slider.Minimum + (percent * range);

        // 5刻み(5の倍数)に丸める
        double roundedValue = Math.Round(rawValue / 5.0) * 5.0;
        roundedValue = Math.Clamp(roundedValue, slider.Minimum, slider.Maximum);

        slider.Value = roundedValue;
    }
}
