namespace FastBlur.Core.Models;

/// <summary>Video metadata from probing a file.</summary>
public sealed class VideoInfo
{
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    /// <summary>e.g. h264, hevc, vp9</summary>
    public string Codec { get; set; } = "";
    /// <summary>Container, e.g. mp4, mkv, mov — normalised to the file extension.</summary>
    public string Format { get; set; } = "";

    public bool IsValid => Duration > 0 && Width > 0 && Height > 0;
}
