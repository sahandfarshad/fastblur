namespace FastBlur.Core.Models;

/// <summary>Persisted user settings (config.json in the data dir).</summary>
public sealed class AppConfig
{
    /// <summary>Seconds between scanned frames (0.5-2).</summary>
    public double ScanInterval { get; set; } = 1.0;
    /// <summary>boxblur radius (10-30).</summary>
    public int BlurStrength { get; set; } = 20;
    /// <summary>Custom case-insensitive substrings to blur.</summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>Pattern toggles: url, email, password, apikey, crypto, suspicious.</summary>
    public Dictionary<string, bool> Patterns { get; set; } = DefaultPatterns();

    /// <summary>Manual tool paths — empty means auto-detect.</summary>
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string TesseractPath { get; set; } = "";

    public static Dictionary<string, bool> DefaultPatterns() => new()
    {
        ["url"] = true,
        ["email"] = true,
        ["password"] = true,
        ["apikey"] = true,
        ["crypto"] = true,
        // Opt-in: long random-looking tokens. Default OFF — too noisy on its own.
        ["suspicious"] = false,
    };

    /// <summary>Clamps out-of-range values back to the defaults, like the Go version.</summary>
    public void Normalize()
    {
        if (ScanInterval is < 0.5 or > 2) ScanInterval = 1.0;
        if (BlurStrength is < 10 or > 30) BlurStrength = 20;
        Keywords ??= new();
        Patterns ??= DefaultPatterns();
        foreach (var key in DefaultPatterns().Keys)
            Patterns.TryAdd(key, DefaultPatterns()[key]);
    }
}
