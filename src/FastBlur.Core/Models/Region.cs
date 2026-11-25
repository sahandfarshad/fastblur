namespace FastBlur.Core.Models;

/// <summary>One blur target: a box (pixels + relative 0-1) over a time range.</summary>
public class Region
{
    public string Id { get; set; } = "";
    /// <summary>url, email, password, apikey, crypto, keyword, possible-key, manual</summary>
    public string Label { get; set; } = "";
    /// <summary>Matched text (redacted in the UI if long).</summary>
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    /// <summary>Relative coordinates, 0-1.</summary>
    public double Rx { get; set; }
    public double Ry { get; set; }
    public double Rw { get; set; }
    public double Rh { get; set; }
    /// <summary>Start time in seconds.</summary>
    public double Start { get; set; }
    /// <summary>End time in seconds.</summary>
    public double End { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>auto | manual</summary>
    public string Source { get; set; } = "manual";

    public Region Clone() => (Region)MemberwiseClone();
}

/// <summary>One merged scan hit; converted to a <see cref="Region"/> on scan completion.</summary>
public sealed class Detection : Region
{
    public int Frames { get; set; }
}
