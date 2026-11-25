using System.Text;
using FastBlur.Core.Detect;
using FastBlur.Core.Models;

namespace FastBlur.Core.Ffmpeg;

/// <summary>
/// Builds the ffmpeg filter_complex string for the blur export.
/// Port of BlurCut's buildFilterComplex / clampRegion.
/// </summary>
public static class FilterBuilder
{
    public static List<Region> EnabledRegions(IReadOnlyList<Region> regions) =>
        regions.Where(r => r.Enabled && r.W > 0 && r.H > 0 && r.End > r.Start).ToList();

    /// <summary>
    /// Pattern: split base into N+1, crop+boxblur each region, overlay them
    /// back in order with between(t,start,end), then force the output to
    /// even dimensions with format=yuv420p — libx264 (and NVENC) reject odd
    /// widths/heights with error -22 ("width not divisible by 2"), and
    /// cropped/window recordings are often odd-sized (e.g. 1279x719).
    /// The final scale is a no-op when the frame is already even.
    /// Returns "" when there are no enabled regions.
    /// </summary>
    public static string BuildFilterComplex(IReadOnlyList<Region> regions, int blurStrength, int frameW, int frameH)
    {
        var clamped = new List<Region>();
        foreach (var r in EnabledRegions(regions))
        {
            var copy = r.Clone();
            if (RegionGeometry.TryClampRegion(copy, frameW, frameH))
                clamped.Add(copy);
        }
        if (clamped.Count == 0) return "";

        var b = new StringBuilder();
        // [0:v]split=N+1[s0][s1]...[sN]
        b.Append("[0:v]split=").Append(clamped.Count + 1);
        for (int i = 0; i <= clamped.Count; i++) b.Append($"[s{i}]");
        b.Append(';');
        for (int i = 0; i < clamped.Count; i++)
        {
            var r = clamped[i];
            // boxblur radius must stay below half the smallest side of the
            // plane it blurs (ffmpeg rejects larger values with -22). A
            // single text line can be ~20px tall, so strength 20 would fail
            // the whole export on small regions — clamp the radii per region
            // instead. Chroma planes are half-resolution in yuv420p, so
            // their limit is a quarter of the box size.
            int luma = Math.Max(0, Math.Min(blurStrength, Math.Min(r.W / 2 - 1, r.H / 2 - 1)));
            int chroma = Math.Max(0, Math.Min(blurStrength, Math.Min(10, Math.Min(r.W / 4 - 1, r.H / 4 - 1))));
            b.Append($"[s{i + 1}]crop={r.W}:{r.H}:{r.X}:{r.Y},boxblur=luma_radius={luma}:luma_power=1:chroma_radius={chroma}:chroma_power=1[b{i}];");
        }
        string cur = "s0";
        for (int i = 0; i < clamped.Count; i++)
        {
            var r = clamped[i];
            string next = $"o{i}";
            b.Append($"[{cur}][b{i}]overlay={r.X}:{r.Y}:enable='between(t,{r.Start:F2},{r.End:F2})'[{next}];");
            cur = next;
        }
        b.Append($"[{cur}]scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p[vout]");
        return b.ToString();
    }

    /// <summary>
    /// Whether the video can be copied straight into an mp4 container:
    /// h264 video AND even dimensions. An odd-dimension remux produces an
    /// mp4 that libx264/yuv420p (and some players) reject, so odd videos
    /// fall through to the re-encode path instead.
    /// </summary>
    public static bool CanRemuxPreview(VideoInfo info)
    {
        var c = info.Codec.ToLowerInvariant();
        if (c != "h264" && c != "avc") return false;
        return info.Width % 2 == 0 && info.Height % 2 == 0;
    }

    /// <summary>
    /// The -vf for the preview re-encode path. Both output dimensions are
    /// forced even (libx264 + yuv420p need that), capped at 1280 wide,
    /// aspect kept.
    /// </summary>
    public static string PreviewScaleFilter() => "scale='trunc(min(1280,iw)/2)*2':-2,format=yuv420p";

    /// <summary>Parses "HH:MM:SS.xx" from ffmpeg stderr into seconds.</summary>
    public static bool TryParseProgressTime(string line, out double seconds)
    {
        seconds = 0;
        var m = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d+):(\d+):([\d.]+)");
        if (!m.Success) return false;
        double h = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double mi = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        double sec = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        seconds = h * 3600 + mi * 60 + sec;
        return true;
    }
}
