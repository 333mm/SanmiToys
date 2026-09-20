using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SanmiToys.Core.Services;
using Wpf.Ui.Appearance;

namespace SanmiToys.Host.Services;

/// <summary>
/// Microsoft Store ポリシー 10.1.1.3 (Inaccurate Representation) に準拠するため、
/// 実際のアプリケーション画面を英語ロケール・1920x1080 (16:9) で直接キャプチャするサービス。
/// RenderTargetBitmap を使用して WPF ビジュアルツリーからピクセルパーフェクトに描画・保存します。
/// </summary>
public static class ScreenshotCaptureService
{
    /// <summary>
    /// 指定されたメインウィンドウから Microsoft Store 用の 6 枚の直接キャプチャスクリーンショットを自動生成します。
    /// </summary>
    /// <param name="mainWindow">キャプチャ対象のメインウィンドウ</param>
    /// <param name="outputDir">画像保存先ディレクトリ</param>
    /// <param name="languageCode">表示言語コード (デフォルト: "en")</param>
    /// <param name="targetWidth">キャプチャ解像度 幅 (デフォルト: 1920)</param>
    /// <param name="targetHeight">キャプチャ解像度 高さ (デフォルト: 1080)</param>
    /// <returns>非同期タスク</returns>
    public static async Task CaptureAllScreenshotsAsync(
        MainWindow mainWindow,
        string outputDir,
        string languageCode = "en",
        int targetWidth = 1920,
        int targetHeight = 1080)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            throw new ArgumentException("Output directory cannot be null or empty.", nameof(outputDir));
        }

        Directory.CreateDirectory(outputDir);

        AppLogger.Info("ScreenshotCapture", $"Starting screenshot capture session (Language: {languageCode}, Resolution: {targetWidth}x{targetHeight})...");

        // 1. 言語の設定（ストア掲載用に英語または指定言語を適用）
        LocalizationService.Instance.CurrentLanguageCode = languageCode;

        // 2. ダークテーマの適用（視認性と Fluent UI の美しいコントラストを確保）
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        // 3. ウィンドウサイズの設定
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Width = targetWidth;
        mainWindow.Height = targetHeight;
        mainWindow.Show();
        mainWindow.Activate();
        mainWindow.UpdateLayout();

        // 初期描画待機（テーマやリソースの解決待ち）
        await Task.Delay(1000);

        // 撮影対象画面の定義 (モジュールID, 出力ファイル名, 説明)
        var screens = new (string ModuleId, string FileName, string Description)[]
        {
            ("Dashboard", "Screenshot_01_Overview.png", "Suite Overview - 5 Powerful Productivity Modules"),
            ("FluidDrag", "Screenshot_02_FluidDrag.png", "FluidDrag - Intuitively Drag Windows Anywhere"),
            ("FocusDimmer", "Screenshot_03_FocusDimmer.png", "FocusDimmer - Dim Background Windows for Deep Focus"),
            ("SnapTrans", "Screenshot_04_SnapTrans.png", "SnapTrans - Snipping OCR, AI Translation & TTS"),
            ("SwiftVolume", "Screenshot_05_SwiftVolume.png", "SwiftVolume - Mouse Wheel Volume HUD & App Mixer"),
            ("OmniGlance", "Screenshot_06_OmniGlance.png", "OmniGlance - Device Batteries & Performance Island"),
        };

        foreach (var screen in screens)
        {
            AppLogger.Info("ScreenshotCapture", $"Capturing screen: {screen.ModuleId} -> {screen.FileName} ({screen.Description})");

            // 画面遷移
            mainWindow.NavigateToModule(screen.ModuleId);
            if (screen.ModuleId == "Dashboard")
            {
                mainWindow.RefreshDashboardState();
            }

            // UIレイアウトとアニメーションの完了を待機
            await Task.Delay(800);
            mainWindow.UpdateLayout();

            string destFile = Path.Combine(outputDir, screen.FileName);
            CaptureVisualToFile(mainWindow, destFile, targetWidth, targetHeight);

            // Dashboard の場合は Screenshot_01_Dashboard.png にもエイリアスコピーを保存
            if (screen.FileName == "Screenshot_01_Overview.png")
            {
                string dashboardAlias = Path.Combine(outputDir, "Screenshot_01_Dashboard.png");
                File.Copy(destFile, dashboardAlias, true);
            }
        }

        AppLogger.Info("ScreenshotCapture", $"All screenshots captured successfully in {outputDir}");
    }

    /// <summary>
    /// WPF Visual を 1920x1080 の RenderTargetBitmap としてメモリ上に描画し、PNG として保存します。
    /// </summary>
    private static void CaptureVisualToFile(Visual visual, string destinationPath, int targetWidth, int targetHeight)
    {
        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            // Windows 11 Fluent Dark Mica 背景色 (#202020)
            var bgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));
            drawingContext.DrawRectangle(bgBrush, null, new Rect(0, 0, targetWidth, targetHeight));
            drawingContext.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, targetWidth, targetHeight));
        }

        var renderBmp = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        renderBmp.Render(drawingVisual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBmp));

        using (var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }

        AppLogger.Info("ScreenshotCapture", $"Successfully saved visual capture: {destinationPath} ({targetWidth}x{targetHeight})");
    }
}
