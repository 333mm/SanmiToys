using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;
using SanmiToys.Modules.SnapTrans.Models;

namespace SanmiToys.Modules.SnapTrans.Services;

public class TranslationService
{
    private static readonly HttpClient _httpClient = new();

    public async Task<string> TranslateAsync(string text, SnapTransSettings settings)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        return settings.Provider switch
        {
            TranslationProviderType.DeepL => await TranslateWithDeepLAsync(text, settings.TargetLanguage, settings.DeepLApiKey),
            TranslationProviderType.Gemini => await TranslateWithGeminiAsync(text, settings.TargetLanguage, settings.GeminiApiKey),
            TranslationProviderType.OpenAI => await TranslateWithOpenAiAsync(text, settings.TargetLanguage, settings.OpenAiApiKey),
            _ => await TranslateWithGoogleWebAsync(text, settings.TargetLanguage)
        };
    }

    private async Task<string> TranslateWithGoogleWebAsync(string text, string targetLang)
    {
        try
        {
            string langCode = targetLang.ToLowerInvariant();
            if (langCode.StartsWith("en")) langCode = "en";
            if (langCode == "auto") langCode = "ja";

            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={langCode}&dt=t&q={HttpUtility.UrlEncode(text)}";
            var json = await _httpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var sentences = root[0];
                var sb = new StringBuilder();
                foreach (var sentence in sentences.EnumerateArray())
                {
                    if (sentence.GetArrayLength() > 0)
                    {
                        sb.Append(sentence[0].GetString());
                    }
                }
                return sb.ToString();
            }
            return text;
        }
        catch (Exception ex)
        {
            return $"[Google Web 翻訳エラー] {ex.Message}";
        }
    }

    private async Task<string> TranslateWithDeepLAsync(string text, string targetLang, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "DeepL APIキーが設定されていません。設定画面で登録してください。";

        try
        {
            string endpoint = apiKey.EndsWith(":fx") 
                ? "https://api-free.deepl.com/v2/translate" 
                : "https://api.deepl.com/v2/translate";

            string lang = targetLang.ToUpperInvariant();
            if (lang == "EN") lang = "EN-US";

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("text", text),
                new KeyValuePair<string, string>("target_lang", lang)
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            return node?["translations"]?[0]?["text"]?.GetValue<string>() ?? text;
        }
        catch (Exception ex)
        {
            return $"[DeepL 翻訳エラー] {ex.Message}";
        }
    }

    private async Task<string> TranslateWithGeminiAsync(string text, string targetLang, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "Gemini APIキーが設定されていません。設定画面で登録してください。";

        try
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}";
            var prompt = $"Translate the following text into {targetLang}. Return ONLY the translated text without commentary:\n\n{text}";

            var payload = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                }
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            return node?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>()?.Trim() ?? text;
        }
        catch (Exception ex)
        {
            return $"[Gemini 翻訳エラー] {ex.Message}";
        }
    }

    private async Task<string> TranslateWithOpenAiAsync(string text, string targetLang, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "OpenAI APIキーが設定されていません。設定画面で登録してください。";

        try
        {
            string url = "https://api.openai.com/v1/chat/completions";
            var payload = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = $"You are a translator. Translate the text into {targetLang}. Return only the translation." },
                    new { role = "user", content = text }
                },
                temperature = 0.3
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim() ?? text;
        }
        catch (Exception ex)
        {
            return $"[OpenAI 翻訳エラー] {ex.Message}";
        }
    }
}
