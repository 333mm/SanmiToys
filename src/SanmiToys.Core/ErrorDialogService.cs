using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfCursors = System.Windows.Input.Cursors;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;
using SanmiToys.Core.Services;

namespace SanmiToys.Core;

public static class ErrorDialogService
{
    public static void ShowError(string title, string message, Exception? ex = null)
    {
        string errorCode = ex != null ? $"0x{ex.HResult:X8}" : "";
        string details = ex != null ? ex.ToString() : "";
        AppLogger.Error("ErrorDialog", $"{title} | {message} | {errorCode}", ex);
        ShowError(title, message, errorCode, details);
    }

    public static void ShowError(string title, string message, string errorCode, string details = "")
    {
        // UIスレッドがハング・フリーズしている場合でも確実にダイアログを表示するため、
        // 独立した STA スレッドでモーダルウィンドウを展開
        try
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    ShowErrorInternal(title, message, errorCode, details);
                }
                catch
                {
                    // フォールバック: Windows Forms MessageBox
                    string fallbackText = $"[SanmiToys エラー]\n\n{title}\n\nエラーコード: {errorCode}\n\n{message}\n\n{details}";
                    System.Windows.Forms.MessageBox.Show(fallbackText, "SanmiToys エラー", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
        catch
        {
            ShowErrorInternal(title, message, errorCode, details);
        }
    }

    private static void ShowErrorInternal(string title, string message, string errorCode, string details)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[SanmiToys エラーレポート]");
        sb.AppendLine($"発生時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"タイトル: {title}");
        if (!string.IsNullOrWhiteSpace(errorCode)) sb.AppendLine($"エラーコード: {errorCode}");
        if (!string.IsNullOrWhiteSpace(message)) sb.AppendLine($"メッセージ: {message}");
        sb.AppendLine($"OSバージョン: {Environment.OSVersion}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.AppendLine($"[詳細情報]");
            sb.AppendLine(details);
        }

        string fullText = sb.ToString().Trim();

        var win = new Window
        {
            Title = "SanmiToys エラー",
            Width = 540,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(32, 32, 32)),
            Foreground = WpfBrushes.White,
            WindowStyle = WindowStyle.SingleBorderWindow
        };

        var rootGrid = new Grid { Margin = new Thickness(20) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var headerSp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var titleSp = new StackPanel { Orientation = WpfOrientation.Horizontal };
        var iconText = new TextBlock
        {
            Text = "⚠",
            FontSize = 22,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(240, 80, 80)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = WpfBrushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleSp.Children.Add(iconText);
        titleSp.Children.Add(titleTb);
        headerSp.Children.Add(titleSp);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var msgTb = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(200, 200, 200)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            headerSp.Children.Add(msgTb);
        }

        Grid.SetRow(headerSp, 0);
        rootGrid.Children.Add(headerSp);

        // Details TextBox
        var border = new Border
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(22, 22, 22)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(60, 60, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8)
        };
        var textBox = new WpfTextBox
        {
            Text = fullText,
            IsReadOnly = true,
            Background = WpfBrushes.Transparent,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(220, 220, 220)),
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new WpfFontFamily("Consolas, Meiryo"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        border.Child = textBox;
        Grid.SetRow(border, 1);
        rootGrid.Children.Add(border);

        // Footer Buttons
        var footerGrid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copyBtn = new WpfButton
        {
            Content = "📋 エラー内容をコピー",
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = WpfCursors.Hand,
            Background = new SolidColorBrush(WpfColor.FromRgb(45, 45, 45)),
            Foreground = WpfBrushes.White,
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(70, 70, 70)),
            HorizontalAlignment = WpfHorizontalAlignment.Left
        };
        copyBtn.Click += (s, e) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(fullText);
                copyBtn.Content = "✓ コピー完了！";
            }
            catch { }
        };
        Grid.SetColumn(copyBtn, 0);
        footerGrid.Children.Add(copyBtn);

        var closeBtn = new WpfButton
        {
            Content = "閉じる",
            Width = 90,
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = WpfCursors.Hand,
            Background = new SolidColorBrush(WpfColor.FromRgb(0, 120, 212)),
            Foreground = WpfBrushes.White,
            BorderThickness = new Thickness(0)
        };
        closeBtn.Click += (s, e) => win.Close();
        Grid.SetColumn(closeBtn, 1);
        footerGrid.Children.Add(closeBtn);

        Grid.SetRow(footerGrid, 2);
        rootGrid.Children.Add(footerGrid);

        win.Content = rootGrid;
        win.ShowDialog();
    }
}
