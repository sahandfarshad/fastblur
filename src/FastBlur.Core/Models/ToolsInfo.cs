namespace FastBlur.Core.Models;

/// <summary>Locations of the external tools FastBlur shells out to.</summary>
public sealed class ToolsInfo
{
    public string FFmpeg { get; set; } = "";
    public string FFprobe { get; set; } = "";
    public string Tesseract { get; set; } = "";
    /// <summary>
    /// Tessdata dir itself (TESSDATA_PREFIX points AT tessdata, not its parent).
    /// Empty = tesseract's default.
    /// </summary>
    public string TessData { get; set; } = "";
    public bool HasNvenc { get; set; }

    /// <summary>Every place that was checked, per tool — shown in the UI when a tool is missing.</summary>
    public Dictionary<string, List<string>> Searched { get; set; } = new();

    public bool FFmpegOk => FFmpeg.Length > 0;
    public bool TesseractOk => Tesseract.Length > 0;

    public string SearchedSummary(string tool) =>
        Searched.TryGetValue(tool, out var list) && list.Count > 0
            ? string.Join(", ", list)
            : "PATH";
}
