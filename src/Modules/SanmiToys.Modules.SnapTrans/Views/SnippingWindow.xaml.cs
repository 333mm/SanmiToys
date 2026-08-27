using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SanmiToys.Core;
using SanmiToys.Core.Helpers;
using SanmiToys.Modules.SnapTrans.Models;
using SanmiToys.Modules.SnapTrans.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;

namespace SanmiToys.Modules.SnapTrans.Views;

public partial class SnippingWindow : Window
{
    private readonly SnapTransSettings _settings;
    private readonly OcrService _ocrService;
    private readonly TranslationService _translationService;
    private readonly TextToSpeechService _ttsService;

    private readonly Bitmap _capturedFullBitmap;
    private readonly System.Drawing.Rectangle _virtualBounds;
    private Point _startPoint;
    private bool _isDragging = false;

    public SnippingWindow(Bitmap capturedFullBitmap, System.Drawing.Rectangle virtualBounds, SnapTransSettings settings, OcrService ocrService, TranslationService translationService, TextToSpeechService ttsService)
    {
        InitializeComponent();
        _settings = settings;
        _ocrService = ocrService;
        _translationService = translationService;
        _ttsService = ttsService;

        _virtualBounds = virtualBounds;
        _capturedFullBitmap = capturedFullBitmap;

        this.Left = _virtualBounds.Left;
        this.Top = _virtualBounds.Top;
        this.Width = _virtualBounds.Width;
        this.Height = _virtualBounds.Height;

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
            if (e.Key == Key.Escape) Close();
        };

        this.Closed += (s, e) =>
        {
            _capturedFullBitmap.Dispose();
        };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        _isDragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _startPoint.X);
        Canvas.SetTop(SelectionRect, _startPoint.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var currentPoint = e.GetPosition(this);
        var x = Math.Min(currentPoint.X, _startPoint.X);
        var y = Math.Min(currentPoint.Y, _startPoint.Y);
        var w = Math.Abs(currentPoint.X - _startPoint.X);
        var h = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;

        var x = Canvas.GetLeft(SelectionRect);
        var y = Canvas.GetTop(SelectionRect);
        var w = SelectionRect.Width;
        var h = SelectionRect.Height;

        if (w < 10 || h < 10)
        {
            Close();
            return;
        }

        // WPF論理座標からビットマップ物理ピクセル座標へ正確にスケール変換（DPIスケーリング完全対応）
        double scaleX = (double)_capturedFullBitmap.Width / Math.Max(1, this.ActualWidth > 0 ? this.ActualWidth : this.Width);
        double scaleY = (double)_capturedFullBitmap.Height / Math.Max(1, this.ActualHeight > 0 ? this.ActualHeight : this.Height);

        int cropX = (int)Math.Max(0, Math.Min(x * scaleX, _capturedFullBitmap.Width - 1));
        int cropY = (int)Math.Max(0, Math.Min(y * scaleY, _capturedFullBitmap.Height - 1));
        int cropW = (int)Math.Min(w * scaleX, _capturedFullBitmap.Width - cropX);
        int cropH = (int)Math.Min(h * scaleY, _capturedFullBitmap.Height - cropY);

        if (cropW < 5 || cropH < 5)
        {
            Close();
            return;
        }

        // クロップビットマップをメモリに抽出
        var croppedBitmap = new Bitmap(cropW, cropH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(croppedBitmap))
        {
            g.DrawImage(_capturedFullBitmap, 
                new System.Drawing.Rectangle(0, 0, cropW, cropH),
                new System.Drawing.Rectangle(cropX, cropY, cropW, cropH),
                GraphicsUnit.Pixel);
        }

        double overlayPosX = this.Left + x;
        double overlayPosY = this.Top + y + h + 10;

        // スニッピング画面を即座に破棄・終了（一切のフリーズを根絶）
        Close();

        // バックグラウンドで完全非同期に OCR & 翻訳を実行
        _ = Task.Run(async () =>
        {
            try
            {
                using (croppedBitmap)
                {
                    var ocrDetails = await _ocrService.PerformOcrDetailedAsync(croppedBitmap, _settings.OcrLanguage);
                    if (!ocrDetails.Success)
                    {
                        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            ErrorDialogService.ShowError(
                                "文字認識に失敗しました",
                                ocrDetails.ErrorMessage,
                                ocrDetails.ErrorCode,
                                ocrDetails.Diagnostics);
                        });
                        return;
                    }

                    string ocrText = ocrDetails.Text;

                    if (_settings.AutoCopyToClipboard && _settings.CopyOcrToClipboard && !_settings.CopyTranslationToClipboard)
                    {
                        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            try { System.Windows.Clipboard.SetText(ocrText); } catch { }
                        });
                    }

                    string translatedText = await _translationService.TranslateAsync(ocrText, _settings);

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
                        overlay.SetPosition(overlayPosX, overlayPosY);
                        overlay.Show();

                        if (_settings.AutoSpeakResult)
                        {
                            _ttsService.Speak(translatedText);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    ErrorDialogService.ShowError(
                        "キャプチャ・翻訳エラー",
                        ex.Message,
                        ex);
                });
            }
        });
    }
}
