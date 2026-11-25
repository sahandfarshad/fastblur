using System.Text.Json;
using FastBlur.Core.Models;

namespace FastBlur.Core.Config;

/// <summary>
/// Persisted settings (config.json in the data dir). Port of BlurCut's config.go.
/// </summary>
public static class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultDataDir()
    {
        var env = Environment.GetEnvironmentVariable("FASTBLUR_DATA");
        if (!string.IsNullOrEmpty(env)) return env;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0) return Path.Combine(localAppData, "FastBlur");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fastblur");
    }

    public static AppConfig Load(string dataDir)
    {
        var cfg = new AppConfig();
        var path = Path.Combine(dataDir, "config.json");
        if (!File.Exists(path)) return cfg;
        try
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
            if (loaded != null) cfg = loaded;
        }
        catch
        {
            // Corrupt config: fall back to defaults rather than crash.
            return new AppConfig();
        }
        cfg.Normalize();
        return cfg;
    }

    public static void Save(string dataDir, AppConfig cfg)
    {
        Directory.CreateDirectory(dataDir);
        var json = JsonSerializer.Serialize(cfg, JsonOptions);
        File.WriteAllText(Path.Combine(dataDir, "config.json"), json);
    }
}
