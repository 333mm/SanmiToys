using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using SanmiToys.Core;
using SanmiToys.Core.Helpers;
using SanmiToys.Modules.SnapTrans.Models;
using SanmiToys.Modules.SnapTrans.Services;

namespace SanmiToys.Modules.SnapTrans.Views;

public partial class SelectionMiniToolbar : Window
{
    private string _selectedText = string.Empty;
    private readonly SnapTransSettings _settings;
    private readonly TranslationService _translationService;
    private readonly TextToSpeechService _ttsService;

    public SelectionMiniToolbar(
        SnapTransSettings settings,
        TranslationService translationService,
        TextToSpeechService ttsService)
    {
        InitializeComponent();

        _settings = settings;
        _translationService = translationService;
        _ttsService = ttsService;

        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

        this.SourceInitialized += (s, e) =>
        {
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        };

        this.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) HideToolbar();
        };
    }

    public void ShowAt(string selectedText, double screenX, double screenY)
    {
        _selectedText = selectedText;
        CopyButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Copy24);

        SetPosition(screenX, screenY);

        if (!this.IsVisible)
        {
            this.Show();
        }
    }

    public void HideToolbar()
    {
        if (this.IsVisible)
        {
            this.Hide();
        }
    }

    public void SetPosition(double screenX, double screenY)
    {
        double estimatedWidth = this.ActualWidth > 0 ? this.ActualWidth : 85;
        double estimatedHeight = this.ActualHeight > 0 ? this.ActualHeight : 42;

        double targetX = screenX - (estimatedWidth / 2);
        double targetY = screenY - estimatedHeight - 10; // カーソル上部

        var virtualBounds = SystemInformation.VirtualScreen;
        // 画面上部に見切れる場合はカーソルの下に配置
        if (targetY < virtualBounds.Top + 10)
        {
            targetY = screenY + 22;
        }

        // 画面外はみ出しをクランプ
        targetX = Math.Max(virtualBounds.Left + 8, Math.Min(targetX, virtualBounds.Right - estimatedWidth - 8));
        targetY = Math.Max(virtualBounds.Top + 8, Math.Min(targetY, virtualBounds.Bottom - estimatedHeight - 8));

        this.Left = targetX;
        this.Top = targetY;
    }

    public bool ContainsScreenPoint(int screenX, int screenY)
    {
        if (!this.IsVisible) return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // 1. マウス位置のウィンドウハンドルがこのツールバー（またはその子要素）か判定
        var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
        var clickedHwnd = NativeMethods.WindowFromPoint(pt);
        if (clickedHwnd != IntPtr.Zero)
        {
            var root = NativeMethods.GetAncestor(clickedHwnd, NativeMethods.GA_ROOT);
            if (root == hwnd || clickedHwnd == hwnd)
            {
                return true;
            }
        }

        // 2. ウィンドウの物理スクリーン矩形内にあるか判定（DPIスケール非依存）
        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return screenX >= rect.Left && screenX <= rect.Right &&
                   screenY >= rect.Top && screenY <= rect.Bottom;
        }

        return false;
    }

    private void OnTranslateClicked(object sender, RoutedEventArgs e)
    {
        double currentX = this.Left;
        double currentY = this.Top;
        string textToTranslate = _selectedText;

        HideToolbar();

        _ = Task.Run(async () =>
        {
            try
            {
                string translatedText = await _translationService.TranslateAsync(textToTranslate, _settings);

                if (_settings.AutoCopyToClipboard && _settings.CopyTranslationToClipboard)
                {
                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        try { System.Windows.Clipboard.SetText(translatedText); } catch { }
                    });
                }

                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    var overlay = new ResultOverlay(translatedText, _ttsService);
                    overlay.SetPosition(currentX, currentY);
                    overlay.Show();
                    try { overlay.Activate(); } catch { }

                    if (_settings.AutoSpeakResult)
                    {
                        _ttsService.Speak(translatedText);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    ErrorDialogService.ShowError("翻訳エラー", ex.Message, ex);
                });
            }
        });
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_selectedText);
            CopyButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Checkmark24);
            await Task.Delay(300);
        }
        catch { }

        HideToolbar();
    }
}
