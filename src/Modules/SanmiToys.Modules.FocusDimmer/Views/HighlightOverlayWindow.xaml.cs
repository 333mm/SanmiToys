using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SanmiToys.Modules.FocusDimmer.Core;

namespace SanmiToys.Modules.FocusDimmer.Views;

public partial class HighlightOverlayWindow : Window
{
    private IntPtr _hwnd = IntPtr.Zero;

    public HighlightOverlayWindow()
    {
        InitializeComponent();
        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

        this.Left = SystemParameters.VirtualScreenLeft;
        this.Top = SystemParameters.VirtualScreenTop;
        this.Width = SystemParameters.VirtualScreenWidth;
        this.Height = SystemParameters.VirtualScreenHeight;

        this.SourceInitialized += (s, e) =>
        {
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.Handle;
            ApplyClickThrough();
            WindowHelper.DisableBackdropAndBlur(_hwnd);
        };

        this.Loaded += (s, e) =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                WindowHelper.DisableBackdropAndBlur(_hwnd);
            }
        };
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        int exStyle = FocusDimmerNativeMethods.GetWindowLong(_hwnd, FocusDimmerNativeMethods.GWL_EXSTYLE);
        FocusDimmerNativeMethods.SetWindowLong(_hwnd, FocusDimmerNativeMethods.GWL_EXSTYLE,
            exStyle | FocusDimmerNativeMethods.WS_EX_TRANSPARENT 
                    | FocusDimmerNativeMethods.WS_EX_LAYERED 
                    | FocusDimmerNativeMethods.WS_EX_TOOLWINDOW 
                    | FocusDimmerNativeMethods.WS_EX_NOACTIVATE);
        FocusDimmerNativeMethods.SetProp(_hwnd, "FocusDimmerInspector", new IntPtr(1));
        WindowHelper.DisableBackdropAndBlur(_hwnd);
    }

    public void UpdateHighlight(Rect targetRect, string tag, string title)
    {
        if (targetRect.Width <= 0 || targetRect.Height <= 0)
        {
            HighlightRect.Visibility = Visibility.Collapsed;
            InfoBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // 仮想スクリーン原点からの相対座標
        double relX = targetRect.Left - this.Left;
        double relY = targetRect.Top - this.Top;

        Canvas.SetLeft(HighlightRect, Math.Max(0, relX));
        Canvas.SetTop(HighlightRect, Math.Max(0, relY));
        HighlightRect.Width = Math.Max(10, targetRect.Width);
        HighlightRect.Height = Math.Max(10, targetRect.Height);
        HighlightRect.Visibility = Visibility.Visible;

        BadgeTag.Text = tag;
        BadgeTitle.Text = string.IsNullOrEmpty(title) ? "(タイトルなし)" : title;

        double badgeX = Math.Max(0, relX);
        double badgeY = relY > 30 ? relY - 28 : relY + targetRect.Height + 4;
        Canvas.SetLeft(InfoBadge, badgeX);
        Canvas.SetTop(InfoBadge, badgeY);
        InfoBadge.Visibility = Visibility.Visible;
    }

    public void ClearHighlight()
    {
        HighlightRect.Visibility = Visibility.Collapsed;
        InfoBadge.Visibility = Visibility.Collapsed;
    }
}
