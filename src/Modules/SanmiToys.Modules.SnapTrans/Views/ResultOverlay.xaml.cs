using System;
using System.Windows;
using System.Windows.Input;
using SanmiToys.Modules.SnapTrans.Services;

namespace SanmiToys.Modules.SnapTrans.Views;

public partial class ResultOverlay : Window
{
    private readonly string _text;
    private readonly TextToSpeechService? _ttsService;

    public ResultOverlay(string text, TextToSpeechService? ttsService = null)
    {
        InitializeComponent();

        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

        _text = text;
        _ttsService = ttsService;
        ResultTextBox.Text = text;

        this.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        this.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        };
    }

    public void SetPosition(double screenX, double screenY)
    {
        this.Left = Math.Max(10, screenX);
        this.Top = Math.Max(10, screenY);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_text);
            CopyButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Checkmark24);
            await System.Threading.Tasks.Task.Delay(1200);
            CopyButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Copy24);
        }
        catch { }
    }

    private void OnSpeakClicked(object sender, RoutedEventArgs e)
    {
        _ttsService?.Speak(_text);
    }
}
