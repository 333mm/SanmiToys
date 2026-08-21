namespace SanmiToys.Modules.SnapTrans.Models;

public enum TranslationProviderType
{
    GoogleWeb,
    DeepL,
    Gemini,
    OpenAI
}

public class SnapTransSettings
{
    public bool IsEnabled { get; set; } = false;
    public TranslationProviderType Provider { get; set; } = TranslationProviderType.GoogleWeb;
    public string TargetLanguage { get; set; } = "ja";
    public string OcrLanguage { get; set; } = "Auto";
    public string DeepLApiKey { get; set; } = "";
    public string GeminiApiKey { get; set; } = "";
    public string OpenAiApiKey { get; set; } = "";
    
    // クリップボード自動コピー設定
    public bool AutoCopyToClipboard { get; set; } = true;
    public bool CopyOcrToClipboard { get; set; } = false;
    public bool CopyTranslationToClipboard { get; set; } = true;
    
    public bool AutoSpeakResult { get; set; } = false;
    public double OverlayFontSize { get; set; } = 14.0;
    public double OverlayOpacity { get; set; } = 95.0;

    // ホットキー設定（デフォルト: Ctrl + Shift + T）
    public bool HotkeyCtrl { get; set; } = true;
    public bool HotkeyAlt { get; set; } = false;
    public bool HotkeyShift { get; set; } = true;
    public bool HotkeyWin { get; set; } = false;
    public string HotkeyKey { get; set; } = "T";
}
