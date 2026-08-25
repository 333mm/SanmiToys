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
    private static readonly HttpClient _httpClient;

    static TranslationService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

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
        string langCode = targetLang.ToLowerInvariant();
        if (langCode.StartsWith("en")) langCode = "en";
        if (langCode == "auto") langCode = "ja";
        string encoded = HttpUtility.UrlEncode(text);

        // エンドポイント 1: translate.googleapis.com (gtx)
        try
        {
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={langCode}&dt=t&q={encoded}";
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
                string result = sb.ToString();
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
        }
        catch { }

        // エンドポイント 2: clients5.google.com (dict-chrome-ex) - 429 制限を回避しやすい
        try
        {
            string url2 = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl={langCode}&q={encoded}";
            var json2 = await _httpClient.GetStringAsync(url2);
            using var doc2 = JsonDocument.Parse(json2);
            var root2 = doc2.RootElement;
            if (root2.ValueKind == JsonValueKind.Array && root2.GetArrayLength() > 0)
            {
                var sb = new StringBuilder();
                foreach (var elem in root2.EnumerateArray())
                {
                    if (elem.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(elem.GetString());
                    }
                    else if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
                    {
                        sb.Append(elem[0].GetString());
                    }
                }
                string result = sb.ToString();
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
            else if (root2.ValueKind == JsonValueKind.String)
            {
                return root2.GetString() ?? text;
            }
        }
        catch { }

        // エンドポイント 3: translate.googleapis.com (dict-chrome-ex)
        try
        {
            string url3 = $"https://translate.googleapis.com/translate_a/single?client=dict-chrome-ex&sl=auto&tl={langCode}&dt=t&q={encoded}";
            var json3 = await _httpClient.GetStringAsync(url3);
            using var doc3 = JsonDocument.Parse(json3);
            var root3 = doc3.RootElement;
            if (root3.ValueKind == JsonValueKind.Array && root3.GetArrayLength() > 0)
            {
                var sentences = root3[0];
                var sb = new StringBuilder();
                foreach (var sentence in sentences.EnumerateArray())
                {
                    if (sentence.GetArrayLength() > 0)
                    {
                        sb.Append(sentence[0].GetString());
                    }
                }
                string result = sb.ToString();
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
        }
        catch { }

        // エンドポイント 4: MyMemory 無料翻訳 API へのフォールバック
        try
        {
            string myMemoryUrl = $"https://api.mymemory.translated.net/get?q={encoded}&langpair=autodetect|{langCode}";
            var json = await _httpClient.GetStringAsync(myMemoryUrl);
            var node = JsonNode.Parse(json);
            string? translated = node?["responseData"]?["translatedText"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return HttpUtility.HtmlDecode(translated);
            }
        }
        catch (Exception ex)
        {
            return $"[Google Web 翻訳エラー] リクエストが混み合っています。しばらく待ってから再試行してください ({ex.Message})";
        }

        return text;
    }

    private async Task<string> TranslateWithDeepLAsync(string text, string targetLang, string apiKey)
    {
        string key = (apiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key)) return "DeepL APIキーが設定されていません。設定画面で登録してください。";

        try
        {
            string endpoint = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) 
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
            request.Headers.Add("Authorization", $"DeepL-Auth-Key {key}");
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errBody = await response.Content.ReadAsStringAsync();
                return $"[DeepL 翻訳エラー HTTP {(int)response.StatusCode}] {errBody}";
            }

            var json = await response.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            return node?["translations"]?[0]?["text"]?.GetValue<string>() ?? text;
        }
        catch (Exception ex)
        {
            return $"[DeepL 翻訳エラー] {ex.Message}";
        }
    }

    private static string? _cachedGeminiModel = null;
    private static DateTime _lastModelDiscoveryTime = DateTime.MinValue;

    private async Task<List<string>> GetGeminiModelCandidatesAsync(string key)
    {
        if (!string.IsNullOrEmpty(_cachedGeminiModel) && (DateTime.UtcNow - _lastModelDiscoveryTime).TotalHours < 1)
        {
            return new List<string> { _cachedGeminiModel, "gemini-3.5-flash-lite", "gemini-2.5-flash-lite", "gemini-2.5-flash", "gemini-2.0-flash", "gemini-1.5-flash" };
        }

        var candidates = new List<string>();
        try
        {
            string listUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}";
            using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
            listReq.Headers.Add("x-goog-api-key", key);
            var listResp = await _httpClient.SendAsync(listReq);
            if (listResp.IsSuccessStatusCode)
            {
                var listJson = await listResp.Content.ReadAsStringAsync();
                var listDoc = JsonNode.Parse(listJson);
                var modelsArray = listDoc?["models"]?.AsArray();
                if (modelsArray != null)
                {
                    var availableModels = new List<string>();
                    foreach (var m in modelsArray)
                    {
                        string? name = m?["name"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name)) continue;
                        string mId = name.StartsWith("models/") ? name.Substring("models/".Length) : name;

                        var methods = m?["supportedGenerationMethods"]?.AsArray();
                        bool supportsGen = false;
                        if (methods != null)
                        {
                            foreach (var method in methods)
                            {
                                if (method?.GetValue<string>() == "generateContent") { supportsGen = true; break; }
                            }
                        }

                        if (supportsGen && mId.Contains("flash", StringComparison.OrdinalIgnoreCase))
                        {
                            availableModels.Add(mId);
                        }
                    }

                    var sorted = availableModels
                        .OrderByDescending(m => m.Contains("3.5", StringComparison.OrdinalIgnoreCase) && m.Contains("lite", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(m => m.Contains("2.5", StringComparison.OrdinalIgnoreCase) && m.Contains("lite", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(m => m.Contains("3.5", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(m => m.Contains("2.5", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(m => m.Contains("2.0", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(m => m.Contains("1.5", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (sorted.Count > 0)
                    {
                        _cachedGeminiModel = sorted[0];
                        _lastModelDiscoveryTime = DateTime.UtcNow;
                        candidates.AddRange(sorted);
                    }
                }
            }
        }
        catch { }

        var fallbackList = new[]
        {
            "gemini-3.5-flash-lite",
            "gemini-2.5-flash-lite",
            "gemini-2.5-flash",
            "gemini-2.0-flash",
            "gemini-1.5-flash"
        };
        foreach (var fb in fallbackList)
        {
            if (!candidates.Contains(fb)) candidates.Add(fb);
        }

        return candidates;
    }

    private async Task<string> TranslateWithGeminiAsync(string text, string targetLang, string apiKey)
    {
        string key = (apiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key)) return "Gemini APIキーが設定されていません。設定画面で登録してください。";

        string prompt = $"Translate the following text into {targetLang}. Return ONLY the translated text without commentary:\n\n{text}";
        var payload = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            }
        };
        var jsonPayload = JsonSerializer.Serialize(payload);

        var modelCandidates = await GetGeminiModelCandidatesAsync(key);
        string lastError = "";

        foreach (var model in modelCandidates)
        {
            foreach (var version in new[] { "v1beta", "v1" })
            {
                try
                {
                    string encodedKey = Uri.EscapeDataString(key);
                    string url = $"https://generativelanguage.googleapis.com/{version}/models/{model}:generateContent?key={encodedKey}";
                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("x-goog-api-key", key);
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var node = JsonNode.Parse(json);
                        string? translated = node?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>()?.Trim();
                        if (!string.IsNullOrWhiteSpace(translated))
                        {
                            _cachedGeminiModel = model;
                            _lastModelDiscoveryTime = DateTime.UtcNow;
                            return translated;
                        }
                    }
                    else
                    {
                        string errBody = await response.Content.ReadAsStringAsync();
                        try
                        {
                            var errNode = JsonNode.Parse(errBody);
                            string? msg = errNode?["error"]?["message"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(msg)) lastError = $"{msg} ({model})";
                            else lastError = $"HTTP {(int)response.StatusCode} ({model})";
                        }
                        catch
                        {
                            lastError = $"HTTP {(int)response.StatusCode} ({model})";
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
        }

        return $"[Gemini 翻訳エラー] {lastError}";
    }

    private async Task<string> TranslateWithOpenAiAsync(string text, string targetLang, string apiKey)
    {
        string key = (apiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key)) return "OpenAI APIキーが設定されていません。設定画面で登録してください。";

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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errBody = await response.Content.ReadAsStringAsync();
                try
                {
                    var errNode = JsonNode.Parse(errBody);
                    string? msg = errNode?["error"]?["message"]?.GetValue<string>();
                    return $"[OpenAI 翻訳エラー HTTP {(int)response.StatusCode}] {(msg ?? errBody)}";
                }
                catch
                {
                    return $"[OpenAI 翻訳エラー HTTP {(int)response.StatusCode}] {errBody}";
                }
            }

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
