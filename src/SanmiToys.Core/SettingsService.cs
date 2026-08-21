using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SanmiToys.Core.Services;

public class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _settingsFolder;
    private readonly string _settingsFilePath;
    private JsonObject _rootObject = new();

    public event Action<string>? SettingsChanged;

    private readonly object _lock = new();

    public SettingsService()
    {
        _settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SanmiToys");
        _settingsFilePath = Path.Combine(_settingsFolder, "settings.json");
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    _rootObject = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
                }
                else
                {
                    _rootObject = new JsonObject();
                }
            }
            catch
            {
                _rootObject = new JsonObject();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_settingsFolder))
                {
                    Directory.CreateDirectory(_settingsFolder);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = _rootObject.ToJsonString(options);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Save failed: {ex.Message}");
            }
        }
    }

    public T GetModuleSettings<T>(string moduleId) where T : new()
    {
        lock (_lock)
        {
            if (_rootObject.TryGetPropertyValue(moduleId, out var node) && node != null)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(node.ToJsonString()) ?? new T();
                }
                catch
                {
                    return new T();
                }
            }
            return new T();
        }
    }

    public void SetModuleSettings<T>(string moduleId, T settings)
    {
        lock (_lock)
        {
            var node = JsonSerializer.SerializeToNode(settings);
            _rootObject[moduleId] = node;
            
            // settings オブジェクト内の IsEnabled プロパティも同期抽出
            if (node is JsonObject obj && obj.TryGetPropertyValue("IsEnabled", out var isEnabledNode))
            {
                _rootObject[$"{moduleId}_Enabled"] = isEnabledNode?.GetValue<bool>() ?? false;
            }

            Save();
        }
        SettingsChanged?.Invoke(moduleId);
    }

    public bool IsModuleEnabled(string moduleId, bool defaultValue = false)
    {
        lock (_lock)
        {
            if (_rootObject.TryGetPropertyValue($"{moduleId}_Enabled", out var node) && node != null)
            {
                return node.GetValue<bool>();
            }
            if (_rootObject.TryGetPropertyValue(moduleId, out var modNode) && modNode is JsonObject obj && obj.TryGetPropertyValue("IsEnabled", out var enNode))
            {
                return enNode?.GetValue<bool>() ?? defaultValue;
            }
            return defaultValue;
        }
    }

    public void SetModuleEnabled(string moduleId, bool enabled)
    {
        lock (_lock)
        {
            _rootObject[$"{moduleId}_Enabled"] = enabled;
            if (_rootObject.TryGetPropertyValue(moduleId, out var modNode) && modNode is JsonObject obj)
            {
                obj["IsEnabled"] = enabled;
            }
            Save();
        }
        SettingsChanged?.Invoke(moduleId);
    }

    public T GetGeneralSetting<T>(string key, T defaultValue = default!)
    {
        lock (_lock)
        {
            if (_rootObject.TryGetPropertyValue($"General_{key}", out var node) && node != null)
            {
                try
                {
                    return node.GetValue<T>();
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }
    }

    public void SetGeneralSetting<T>(string key, T value)
    {
        lock (_lock)
        {
            _rootObject[$"General_{key}"] = JsonValue.Create(value);
            Save();
        }
        SettingsChanged?.Invoke($"General_{key}");
    }
}
