using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SanmiToys.Modules.SnapTrans.Services;

public class OcrResultDetails
{
    public bool Success => !string.IsNullOrWhiteSpace(Text);
    public string Text { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string Diagnostics { get; set; } = "";
}

public class OcrService
{
    public async Task<OcrResultDetails> PerformOcrDetailedAsync(Bitmap sourceBitmap, string targetLangTag = "Auto")
    {
        var details = new OcrResultDetails();
        var diag = new StringBuilder();

        diag.AppendLine($"[OCR 診断情報]");
        diag.AppendLine($"入力画像サイズ: {sourceBitmap.Width}x{sourceBitmap.Height}, PixelFormat: {sourceBitmap.PixelFormat}");

        var availableLangs = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        diag.AppendLine($"利用可能な Windows OCR 言語パック: {(availableLangs.Count > 0 ? string.Join(", ", availableLangs) : "なし (0件)")}");

        if (availableLangs.Count == 0)
        {
            details.ErrorCode = "0x80004005 (E_FAIL)";
            details.ErrorMessage = "Windows に OCR 言語パックがインストールされていません。Windows の「設定 > 時刻と言語 > 言語」から日本語または英語の音声認識・文字認識パックを追加してください。";
            details.Diagnostics = diag.ToString();
            return details;
        }

        Bitmap? scaledBitmap = null;
        try
        {
            // 小さい文字の認識精度向上のため、適切な倍率を計算 (フォントの高さが35px〜45px以上になるよう適応的に2.0x〜4.5xに拡大)
            double scale = 2.8;
            if (sourceBitmap.Height < 40 || sourceBitmap.Width < 80)
            {
                scale = Math.Clamp(Math.Max(200.0 / Math.Max(1, sourceBitmap.Height), 300.0 / Math.Max(1, sourceBitmap.Width)), 3.5, 4.5);
            }
            else if (sourceBitmap.Height < 80 || sourceBitmap.Width < 160)
            {
                scale = 4.0;
            }
            else if (sourceBitmap.Height < 160 || sourceBitmap.Width < 320)
            {
                scale = 3.5;
            }
            else if (sourceBitmap.Height < 300 || sourceBitmap.Width < 600)
            {
                scale = 2.8;
            }
            else if (sourceBitmap.Height > 800 || sourceBitmap.Width > 1600)
            {
                scale = 1.5;
            }

            int targetW = (int)Math.Round(sourceBitmap.Width * scale);
            int targetH = (int)Math.Round(sourceBitmap.Height * scale);

            // 最大解像度保護 (クラッシュ防止)
            if (targetW > 3800 || targetH > 3800)
            {
                double capScale = Math.Min(3800.0 / targetW, 3800.0 / targetH);
                targetW = (int)Math.Round(targetW * capScale);
                targetH = (int)Math.Round(targetH * capScale);
            }

            scaledBitmap = CreateEnhancedBitmap(sourceBitmap, targetW, targetH, enhanceContrast: true, applySharpen: true);
            diag.AppendLine($"適応的画像拡大・アンシャープマスク・コントラスト最適化: {sourceBitmap.Width}x{sourceBitmap.Height} -> {targetW}x{targetH} (x{scale:F1})");

            using var softwareBitmap = await ConvertToSoftwareBitmapAsync(scaledBitmap);

            OcrEngine? engine = null;

            if (!string.IsNullOrEmpty(targetLangTag) && targetLangTag != "Auto")
            {
                try
                {
                    var lang = new Language(targetLangTag);
                    if (OcrEngine.IsLanguageSupported(lang))
                    {
                        engine = OcrEngine.TryCreateFromLanguage(lang);
                        diag.AppendLine($"指定言語エンジン使用: {targetLangTag}");
                    }
                }
                catch (Exception ex)
                {
                    diag.AppendLine($"指定言語エンジン作成失敗 ({targetLangTag}): {ex.Message} (0x{ex.HResult:X8})");
                }
            }

            if (engine == null)
            {
                engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine != null)
                {
                    diag.AppendLine($"ユーザー言語エンジン使用: {engine.RecognizerLanguage.LanguageTag}");
                }
            }

            if (engine != null)
            {
                try
                {
                    var result = await engine.RecognizeAsync(softwareBitmap);
                    if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        details.Text = FormatOcrResult(result);
                        details.Diagnostics = diag.ToString();
                        return details;
                    }
                    diag.AppendLine($"第1エンジン認識文字数: 0 (フォールバック試行)");
                }
                catch (Exception ex)
                {
                    details.ErrorCode = $"0x{ex.HResult:X8}";
                    details.ErrorMessage = ex.Message;
                    diag.AppendLine($"第1エンジン認識例外: {ex.Message} (0x{ex.HResult:X8})");
                }
            }

            // フォールバック 1: 利用可能な全認識言語で最善の結果を探す
            string bestText = "";
            int maxScore = -1;

            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                try
                {
                    var fallbackEngine = OcrEngine.TryCreateFromLanguage(lang);
                    if (fallbackEngine != null)
                    {
                        var result = await fallbackEngine.RecognizeAsync(softwareBitmap);
                        if (result != null && result.Text.Length > maxScore)
                        {
                            maxScore = result.Text.Length;
                            bestText = FormatOcrResult(result);
                            diag.AppendLine($"フォールバックエンジン ({lang.LanguageTag}) 認識成功: {bestText.Length} 文字");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(details.ErrorCode)) details.ErrorCode = $"0x{ex.HResult:X8}";
                    diag.AppendLine($"フォールバックエンジン ({lang.LanguageTag}) 例外: {ex.Message} (0x{ex.HResult:X8})");
                }
            }

            if (!string.IsNullOrWhiteSpace(bestText))
            {
                details.Text = bestText;
                details.Diagnostics = diag.ToString();
                return details;
            }

            // フォールバック 2: 元サイズ (1.0x) または別倍率での再試行
            if (scale > 1.2)
            {
                using var originalSoftwareBitmap = await ConvertToSoftwareBitmapAsync(sourceBitmap);
                if (engine != null)
                {
                    try
                    {
                        var rawResult = await engine.RecognizeAsync(originalSoftwareBitmap);
                        if (rawResult != null && !string.IsNullOrWhiteSpace(rawResult.Text))
                        {
                            details.Text = FormatOcrResult(rawResult);
                            details.Diagnostics = diag.ToString();
                            return details;
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(details.ErrorCode))
            {
                details.ErrorCode = "0x80004004 (E_NOTEXT)";
                details.ErrorMessage = "選択した領域内に文字が検出されませんでした。文字のコントラストが低いか、範囲が狭すぎる可能性があります。";
            }
            details.Diagnostics = diag.ToString();
            return details;
        }
        catch (Exception ex)
        {
            details.ErrorCode = $"0x{ex.HResult:X8}";
            details.ErrorMessage = ex.Message;
            diag.AppendLine($"OCR パイプライン全体例外: {ex.ToString()}");
            details.Diagnostics = diag.ToString();
            return details;
        }
        finally
        {
            scaledBitmap?.Dispose();
        }
    }

    private static Bitmap CreateEnhancedBitmap(Bitmap src, int width, int height, bool enhanceContrast, bool applySharpen = true)
    {
        var dst = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            if (enhanceContrast)
            {
                // コントラストを 25% 引き上げ、文字の輪郭と背景を鮮明に分離
                float c = 1.25f;
                float t = (1.0f - c) / 2.0f;
                var colorMatrix = new ColorMatrix(new float[][]
                {
                    new float[] { c, 0, 0, 0, 0 },
                    new float[] { 0, c, 0, 0, 0 },
                    new float[] { 0, 0, c, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { t, t, t, 0, 1 }
                });
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(src, new Rectangle(0, 0, width, height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attributes);
            }
            else
            {
                g.DrawImage(src, 0, 0, width, height);
            }
        }

        if (applySharpen)
        {
            try
            {
                var sharpened = ApplySharpen(dst, strength: 0.65f);
                dst.Dispose();
                return sharpened;
            }
            catch
            {
                // 例外時はシャープ化前の dst を安全に返却
                return dst;
            }
        }

        return dst;
    }

    private static Bitmap ApplySharpen(Bitmap src, float strength = 0.65f)
    {
        // 3x3 アンシャープマスキング畳み込みフィルターによる極小文字輪郭先鋭化
        int w = src.Width;
        int h = src.Height;
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        BitmapData srcData = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dstData = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            int bytes = Math.Abs(srcData.Stride) * h;
            byte[] rgbValues = new byte[bytes];
            byte[] resultValues = new byte[bytes];

            Marshal.Copy(srcData.Scan0, rgbValues, 0, bytes);
            Array.Copy(rgbValues, resultValues, bytes);

            int stride = srcData.Stride;
            float centerWeight = 1.0f + 4.0f * strength;
            float edgeWeight = -strength;

            for (int y = 1; y < h - 1; y++)
            {
                int rowOffset = y * stride;
                int prevRowOffset = (y - 1) * stride;
                int nextRowOffset = (y + 1) * stride;

                for (int x = 1; x < w - 1; x++)
                {
                    int idx = rowOffset + x * 4;

                    for (int c = 0; c < 3; c++) // B, G, R (Alpha は変更しない)
                    {
                        float val = rgbValues[idx + c] * centerWeight +
                                    (rgbValues[prevRowOffset + x * 4 + c] +
                                     rgbValues[nextRowOffset + x * 4 + c] +
                                     rgbValues[rowOffset + (x - 1) * 4 + c] +
                                     rgbValues[rowOffset + (x + 1) * 4 + c]) * edgeWeight;

                        resultValues[idx + c] = (byte)Math.Clamp(val, 0f, 255f);
                    }
                }
            }

            Marshal.Copy(resultValues, 0, dstData.Scan0, bytes);
        }
        finally
        {
            src.UnlockBits(srcData);
            dst.UnlockBits(dstData);
        }

        return dst;
    }

    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, ImageFormat.Png);
        memoryStream.Position = 0;

        var randomAccessStream = memoryStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        return SoftwareBitmap.Convert(
            softwareBitmap,
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
    }

    private static string FormatOcrResult(OcrResult result)
    {
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            var lineSb = new StringBuilder();
            for (int i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                if (i > 0)
                {
                    var prevWord = line.Words[i - 1];
                    char prevLastChar = prevWord.Text.Length > 0 ? prevWord.Text[^1] : ' ';
                    char currFirstChar = word.Text.Length > 0 ? word.Text[0] : ' ';

                    // 前後の単語の境界文字がどちらもCJK（日本語・中国語等）ならスペースを挟まない
                    if (!IsCjkChar(prevLastChar) || !IsCjkChar(currFirstChar))
                    {
                        lineSb.Append(' ');
                    }
                }
                lineSb.Append(word.Text);
            }
            sb.AppendLine(lineSb.ToString());
        }
        return CleanOcrText(sb.ToString());
    }

    public static string CleanOcrText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var cleanedLines = new List<string>();

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // 連続する空白・タブ・全角スペースを単一スペースに正規化
            line = System.Text.RegularExpressions.Regex.Replace(line, @"[ \t\u3000]+", " ");

            // CJK文字とCJK文字の間の不要な半角スペースを排除 (例: "こ ん に ち は" -> "こんにちは")
            line = System.Text.RegularExpressions.Regex.Replace(
                line, 
                @"(?<=[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uFF00-\uFFEF])\s+(?=[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uFF00-\uFFEF])", 
                "");

            // CJK文字と全角約物（、。！？（）「」等）の間の不要なスペースを排除
            line = System.Text.RegularExpressions.Regex.Replace(
                line, 
                @"(?<=[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uFF00-\uFFEF])\s+([、。！？：；（）「」『』【】・…])", 
                "$1");
            line = System.Text.RegularExpressions.Regex.Replace(
                line, 
                @"([、。！？：；（）「」『』【】・…])\s+(?=[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uFF00-\uFFEF])", 
                "$1");

            cleanedLines.Add(line.Trim());
        }

        return string.Join(Environment.NewLine, cleanedLines);
    }

    private static bool IsCjkChar(char c)
    {
        return (c >= 0x3040 && c <= 0x309F) || // ひらがな
               (c >= 0x30A0 && c <= 0x30FF) || // カタカナ
               (c >= 0x4E00 && c <= 0x9FFF) || // CJK統合漢字
               (c >= 0x3400 && c <= 0x4DBF) || // CJK統合漢字拡張A
               (c >= 0x3000 && c <= 0x303F) || // CJK記号・句読点
               (c >= 0xFF00 && c <= 0xFFEF);   // 全角英数・半角カナ
    }
}
