using FastBlur.Core.Models;

namespace FastBlur.Core.Detect;

/// <summary>
/// Region geometry: pixel/relative conversion, overlap, merging across
/// frames and across scans. Port of the geometry helpers in BlurCut's detect.go.
/// </summary>
public static class RegionGeometry
{
    public static bool BoxesOverlap(Region a, Region b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    public static double IoU(Region a, Region b)
    {
        int ix = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X));
        int iy = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        double inter = (double)ix * iy;
        double union = (double)(a.W * a.H + b.W * b.H) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Merges overlapping regions (same frame), unioning the box.</summary>
    public static List<Region> MergeBoxes(IReadOnlyList<Region> regions)
    {
        var outList = regions.Select(r => r.Clone()).ToList();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < outList.Count && !changed; i++)
            for (int j = i + 1; j < outList.Count; j++)
            {
                if (!BoxesOverlap(outList[i], outList[j])) continue;
                outList[i] = Union(outList[i], outList[j]);
                outList.RemoveAt(j);
                changed = true;
                break;
            }
        }
        return outList;
    }

    private static Region Union(Region a, Region b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.X + a.W, b.X + b.W), bottom = Math.Max(a.Y + a.H, b.Y + b.H);
        a.X = x; a.Y = y; a.W = right - x; a.H = bottom - y;
        if (b.Label.Length > 0 && a.Label.Length == 0) a.Label = b.Label;
        if (a.Text.Length == 0) a.Text = b.Text;
        a.End = Math.Max(a.End, b.End);
        return a;
    }

    /// <summary>
    /// Merges detections across consecutive frames into time ranges:
    /// same label, box IoU &gt; 0.3, gap &lt; 2s.
    /// </summary>
    public static List<Detection> MergeTimeRanges(IReadOnlyList<Detection> detections)
    {
        if (detections.Count == 0) return new();
        var sorted = detections.OrderBy(d => d.Start).ToList();
        var result = new List<Detection>();
        foreach (var d in sorted)
        {
            bool merged = false;
            foreach (var o in result)
            {
                if (o.Label == d.Label && IoU(o, d) > 0.3 && d.Start - o.End < 2)
                {
                    o.End = Math.Max(o.End, d.End);
                    o.Frames += d.Frames;
                    // Keep the larger box.
                    if (d.W * d.H > o.W * o.H)
                    {
                        o.X = d.X; o.Y = d.Y; o.W = d.W; o.H = d.H;
                    }
                    merged = true;
                    break;
                }
            }
            if (!merged) result.Add(d);
        }
        return result;
    }

    /// <summary>
    /// Merges fresh auto-scan results into the existing region list:
    /// manual boxes are always kept, old auto regions are replaced by the
    /// fresh scan, and a fresh region that duplicates a kept manual box is
    /// dropped in favour of the manual one — heavy overlap (IoU &gt; 0.5), or
    /// the same label on a near-identical box (IoU &gt; 0.3).
    /// </summary>
    public static List<Region> MergeScanRegions(IReadOnlyList<Region> existing, IReadOnlyList<Region> fresh)
    {
        var manuals = existing.Where(r => r.Source == "manual").ToList();
        var result = new List<Region>(manuals);
        foreach (var f in fresh)
        {
            bool dup = manuals.Any(m =>
                IoU(m, f) > 0.5 || (m.Label.Length > 0 && m.Label == f.Label && IoU(m, f) > 0.3));
            if (!dup) result.Add(f);
        }
        return result;
    }

    /// <summary>Fills Rx/Ry/Rw/Rh from X/Y/W/H given the frame size.</summary>
    public static void PixelToRelative(Region r, int frameW, int frameH)
    {
        if (frameW <= 0 || frameH <= 0) return;
        r.Rx = (double)r.X / frameW;
        r.Ry = (double)r.Y / frameH;
        r.Rw = (double)r.W / frameW;
        r.Rh = (double)r.H / frameH;
    }

    /// <summary>Fills X/Y/W/H from Rx/Ry/Rw/Rh given the frame size.</summary>
    public static void RelativeToPixel(Region r, int frameW, int frameH)
    {
        r.X = (int)(r.Rx * frameW);
        r.Y = (int)(r.Ry * frameH);
        r.W = (int)(r.Rw * frameW);
        r.H = (int)(r.Rh * frameH);
    }

    /// <summary>Expands a box by <paramref name="pad"/> px, clamped to the frame.</summary>
    public static void PadRegion(Region r, int pad, int frameW, int frameH)
    {
        // Pad first, then clamp the whole padded rect: a box near the left
        // edge must not grow past its padded right edge when X clamps to 0.
        int right = Math.Min(frameW, r.X + r.W + pad);
        int bottom = Math.Min(frameH, r.Y + r.H + pad);
        r.X = Math.Max(0, r.X - pad);
        r.Y = Math.Max(0, r.Y - pad);
        r.W = right - r.X;
        r.H = bottom - r.Y;
    }

    /// <summary>
    /// Clamps a box to the frame bounds. Returns false when the box ends up
    /// empty (fully outside the frame, or smaller than 2x2) — callers drop
    /// such regions instead of failing the whole export.
    /// </summary>
    public static bool TryClampRegion(Region r, int frameW, int frameH)
    {
        if (frameW > 0 && frameH > 0)
        {
            // Shifting a negative X/Y into the frame shrinks the box by
            // the same amount — a box fully outside ends up empty.
            if (r.X < 0) { r.W += r.X; r.X = 0; }
            if (r.Y < 0) { r.H += r.Y; r.Y = 0; }
            if (r.X > frameW - 2 || r.Y > frameH - 2) return false;
            if (r.X + r.W > frameW) r.W = frameW - r.X;
            if (r.Y + r.H > frameH) r.H = frameH - r.Y;
        }
        return r.W >= 2 && r.H >= 2;
    }

    /// <summary>
    /// Validates an edited region time range: end must be after start,
    /// both clamped to the video duration. Returns an error message or "".
    /// </summary>
    public static string ValidateTimeRange(double start, double end, double duration)
    {
        if (end <= start) return "End time must be after the start time.";
        if (start < 0 || start >= duration) return $"Start time must be between 0 and {duration:F1}s.";
        if (end > duration) return $"End time cannot be past the video length ({duration:F1}s).";
        return "";
    }
}
