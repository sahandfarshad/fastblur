using System.Text.Json.Serialization;

namespace FastBlur.Core.Models;

/// <summary>One imported video + its detections/regions/export state.</summary>
public sealed class Job
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasExport { get; set; }
    /// <summary>The file the preview plays is a converted mp4, not the original.</summary>
    public bool PreviewConverted { get; set; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    /// <summary>uploaded, scanning, scanned, exporting, done, error</summary>
    public string Status { get; set; } = "uploaded";
    public double Progress { get; set; }
    public string Message { get; set; } = "";
    public List<Detection> Detections { get; set; } = new();
    public List<Region> Regions { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Paths are derived from the data dir, never persisted.
    [JsonIgnore] public string VideoPath { get; set; } = "";
    [JsonIgnore] public string ExportPath { get; set; } = "";
    [JsonIgnore] public string PreviewPath { get; set; } = "";
}
