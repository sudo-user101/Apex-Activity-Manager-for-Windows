using System.Text.Json;
using System.Text.Json.Nodes;

namespace Apex.Services;

/// Small preferences, in %LOCALAPPDATA%\Apex\settings.json.
internal static class Settings
{
    static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "settings.json");
    static JsonObject? data;

    static JsonObject Data
    {
        get
        {
            if (data != null) return data;
            try { data = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch { }
            return data ??= new JsonObject();
        }
    }

    public static T Get<T>(string key, T fallback)
    {
        try { return Data[key] is { } v ? v.GetValue<T>() : fallback; } catch { return fallback; }
    }

    public static void Set<T>(string key, T value)
    {
        Data[key] = JsonValue.Create(value);
        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}
