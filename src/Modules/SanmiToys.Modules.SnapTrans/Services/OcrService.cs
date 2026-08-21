using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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
            // OcrEngine の最小サイズ制限（最低40px以上）を満たすため、必要に応じて拡大
            int targetW = sourceBitmap.Width;
            int targetH = sourceBitmap.Height;
            if (targetW < 120 || targetH < 60)
            {
                double scale = Math.Max(120.0 / targetW, 60.0 / targetH);
                targetW = (int)(targetW * scale);
                targetH = (int)(targetH * scale);

                scaledBitmap = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(scaledBitmap))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(sourceBitmap, 0, 0, targetW, targetH);
                }
                diag.AppendLine($"サイズ最適化: {targetW}x{targetH}");
            }

            var bitmapToProcess = scaledBitmap ?? sourceBitmap;

            using var memoryStream = new MemoryStream();
            bitmapToProcess.Save(memoryStream, ImageFormat.Png);
            memoryStream.Position = 0;

            var randomAccessStream = memoryStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            // OcrEngine が要求する Bgra8 / Premultiplied に確実に変換
            using var convertedBitmap = SoftwareBitmap.Convert(
                softwareBitmap,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);

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
                    var result = await engine.RecognizeAsync(convertedBitmap);
                    if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        details.Text = FormatOcrResult(result);
                        details.Diagnostics = diag.ToString();
                        return details;
                    }
                    diag.AppendLine($"第1エンジン認識文字数: 0");
                }
                catch (Exception ex)
                {
                    details.ErrorCode = $"0x{ex.HResult:X8}";
                    details.ErrorMessage = ex.Message;
                    diag.AppendLine($"第1エンジン認識例外: {ex.Message} (0x{ex.HResult:X8})");
                }
            }

            // フォールバック: 利用可能な全認識言語で最善の結果を探す
            string bestText = "";
            int maxScore = -1;

            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                try
                {
                    var fallbackEngine = OcrEngine.TryCreateFromLanguage(lang);
                    if (fallbackEngine != null)
                    {
                        var result = await fallbackEngine.RecognizeAsync(convertedBitmap);
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
