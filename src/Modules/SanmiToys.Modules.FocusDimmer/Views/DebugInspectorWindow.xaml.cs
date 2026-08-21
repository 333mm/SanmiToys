using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;
using SanmiToys.Modules.FocusDimmer.Core;

namespace SanmiToys.Modules.FocusDimmer.Views;

public class WindowData
{
    public int Index { get; set; }
    public IntPtr Hwnd { get; set; }
    public string ProcessName { get; set; } = "";
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string RectString { get; set; } = "";
    public Rect WindowRect { get; set; }
    public string Flags { get; set; } = "";
    public string ReasonBadge { get; set; } = "明るい領域";
    public Brush BadgeBackgroundBrush { get; set; } = new SolidColorBrush(Color.FromArgb(40, 0, 210, 255));
    public Brush BadgeForegroundBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0, 229, 255));

    public string IndexNumber => $"#{Index + 1}";
    public string DisplayHeader => $"[{Index + 1}] {ProcessName}";
    public string DisplayDetails => $"タイトル: {(string.IsNullOrEmpty(Title) ? "(タイトルなし)" : Title)}\nクラス: {ClassName} | 座標: {RectString}\nフラグ: {Flags}";
}

public partial class DebugInspectorWindow : Window
{
    public event EventHandler<WindowData>? WindowSelected;
    public event EventHandler<WindowData>? WindowHovered;
    public event EventHandler? CloseRequested;

    public DebugInspectorWindow()
    {
        InitializeComponent();
        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

        this.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        this.SourceInitialized += (s, e) =>
        {
            var helper = new WindowInteropHelper(this);
            WindowHelper.DisableBackdropAndBlur(helper.Handle);
        };
    }

    public void UpdateList(IEnumerable<WindowData> windows)
    {
        WindowList.ItemsSource = windows;
    }

    public void UpdateStatus(string text)
    {
        StatusText.Text = text;
    }

    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is WindowData data)
        {
            WindowSelected?.Invoke(this, data);
        }
    }

    private void ListViewItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListViewItem item && item.DataContext is WindowData data)
        {
            WindowHovered?.Invoke(this, data);
        }
    }
}
