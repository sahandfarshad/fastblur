using FastBlur.Core.Detect;
using FastBlur.Core.Models;

namespace FastBlur.Core.Tests;

public sealed class WordGrouperTests
{
    private static OcrWord W(string text, int x, int y, int w = 40, int h = 12, int conf = 90) =>
        new() { Text = text, X = x, Y = y, W = w, H = h, Conf = conf };

    [Fact]
    public void SameLine_NearbyWords_MergeIntoOneBox()
    {
        var words = new[] { W("https://", 10, 100), W("example.com", 60, 100) };
        var groups = WordGrouper.GroupWords(words, 1);
        Assert.Single(groups);
        Assert.Equal("https:// example.com", groups[0].Text);
        Assert.Equal(10, groups[0].X);
        Assert.Equal(90, groups[0].W); // 60+40-10
    }

    [Fact]
    public void FarApartWords_StaySeparate()
    {
        var words = new[] { W("left", 10, 100), W("right", 500, 100) };
        Assert.Equal(2, WordGrouper.GroupWords(words, 1).Count);
    }

    [Fact]
    public void DifferentLines_StaySeparate()
    {
        var words = new[] { W("top", 10, 100), W("bottom", 10, 200) };
        Assert.Equal(2, WordGrouper.GroupWords(words, 1).Count);
    }

    [Fact]
    public void GapThreshold_ScalesWithUpscaleFactor()
    {
        // At 1x, a 60px gap is too far (gapX=40). At 2x the threshold is 80px, so it merges.
        var words = new[] { W("a", 10, 100, w: 10), W("b", 80, 100, w: 10) };
        Assert.Equal(2, WordGrouper.GroupWords(words, 1).Count);
        Assert.Single(WordGrouper.GroupWords(words, 2));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(WordGrouper.GroupWords(Array.Empty<OcrWord>(), 1));
    }

    [Fact]
    public void MergedBox_KeepsLowestConfidence()
    {
        var words = new[] { W("a", 10, 100, conf: 95), W("b", 55, 100, conf: 60) };
        var groups = WordGrouper.GroupWords(words, 1);
        Assert.Equal(60, groups[0].Conf);
    }
}

public sealed class RegionGeometryTests
{
    private static Region R(int x, int y, int w, int h, string label = "url", double start = 0, double end = 5, string source = "auto") =>
        new() { X = x, Y = y, W = w, H = h, Label = label, Start = start, End = end, Source = source };

    [Fact]
    public void IoU_IdenticalBoxes_IsOne()
    {
        Assert.Equal(1, RegionGeometry.IoU(R(0, 0, 10, 10), R(0, 0, 10, 10)));
    }

    [Fact]
    public void IoU_DisjointBoxes_IsZero()
    {
        Assert.Equal(0, RegionGeometry.IoU(R(0, 0, 10, 10), R(50, 50, 10, 10)));
    }

    [Fact]
    public void MergeBoxes_Overlapping_Unions()
    {
        var merged = RegionGeometry.MergeBoxes(new[] { R(0, 0, 10, 10), R(5, 5, 10, 10), R(100, 100, 10, 10) });
        Assert.Equal(2, merged.Count);
        var big = merged.First(r => r.X == 0);
        Assert.Equal(15, big.W);
        Assert.Equal(15, big.H);
    }

    [Fact]
    public void MergeTimeRanges_SameLabel_CloseFrames_Merge()
    {
        var dets = new[]
        {
            new Detection { X = 10, Y = 10, W = 100, H = 20, Label = "url", Start = 0, End = 1, Frames = 1 },
            new Detection { X = 12, Y = 11, W = 100, H = 20, Label = "url", Start = 1, End = 2, Frames = 1 },
            new Detection { X = 12, Y = 11, W = 100, H = 20, Label = "url", Start = 2, End = 3, Frames = 1 },
        };
        var merged = RegionGeometry.MergeTimeRanges(dets);
        Assert.Single(merged);
        Assert.Equal(0, merged[0].Start);
        Assert.Equal(3, merged[0].End);
        Assert.Equal(3, merged[0].Frames);
    }

    [Fact]
    public void MergeTimeRanges_DifferentLabel_DoesNotMerge()
    {
        var dets = new[]
        {
            new Detection { X = 10, Y = 10, W = 100, H = 20, Label = "url", Start = 0, End = 1, Frames = 1 },
            new Detection { X = 10, Y = 10, W = 100, H = 20, Label = "email", Start = 1, End = 2, Frames = 1 },
        };
        Assert.Equal(2, RegionGeometry.MergeTimeRanges(dets).Count);
    }

    [Fact]
    public void MergeTimeRanges_GapOverTwoSeconds_DoesNotMerge()
    {
        var dets = new[]
        {
            new Detection { X = 10, Y = 10, W = 100, H = 20, Label = "url", Start = 0, End = 1, Frames = 1 },
            new Detection { X = 10, Y = 10, W = 100, H = 20, Label = "url", Start = 5, End = 6, Frames = 1 },
        };
        Assert.Equal(2, RegionGeometry.MergeTimeRanges(dets).Count);
    }

    [Fact]
    public void MergeScanRegions_ManualBoxes_AlwaysKept()
    {
        var existing = new[]
        {
            R(10, 10, 100, 20, "manual", source: "manual"),
            R(200, 200, 50, 20, "url", source: "auto"), // old auto — replaced
        };
        var fresh = new[] { R(300, 300, 60, 20, "email") };
        var merged = RegionGeometry.MergeScanRegions(existing, fresh);
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, r => r.Source == "manual");
        Assert.DoesNotContain(merged, r => r.X == 200);
        Assert.Contains(merged, r => r.X == 300);
    }

    [Fact]
    public void MergeScanRegions_FreshDuplicateOfManual_IsDropped()
    {
        var existing = new[] { R(10, 10, 100, 20, "url", source: "manual") };
        var fresh = new[] { R(12, 11, 98, 19, "url") }; // heavy overlap with the manual box
        var merged = RegionGeometry.MergeScanRegions(existing, fresh);
        Assert.Single(merged);
        Assert.Equal("manual", merged[0].Source);
    }

    [Fact]
    public void PixelRelative_RoundTrip()
    {
        var r = R(100, 50, 200, 40);
        RegionGeometry.PixelToRelative(r, 1000, 500);
        Assert.Equal(0.1, r.Rx);
        Assert.Equal(0.1, r.Ry);
        Assert.Equal(0.2, r.Rw);
        Assert.Equal(0.08, r.Rh);
        var r2 = new Region { Rx = r.Rx, Ry = r.Ry, Rw = r.Rw, Rh = r.Rh };
        RegionGeometry.RelativeToPixel(r2, 1000, 500);
        Assert.Equal(100, r2.X);
        Assert.Equal(50, r2.Y);
        Assert.Equal(200, r2.W);
        Assert.Equal(40, r2.H);
    }

    [Fact]
    public void PadRegion_ClampsToFrame()
    {
        var r = R(2, 2, 10, 10);
        RegionGeometry.PadRegion(r, 8, 100, 100);
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
        Assert.Equal(20, r.W);
    }

    [Theory]
    [InlineData(-50, -50, 10, 10, false)] // fully outside
    [InlineData(0, 0, 1, 1, false)]       // smaller than 2x2
    [InlineData(0, 0, 10, 10, true)]
    [InlineData(95, 95, 20, 20, true)]    // clamped to frame, still valid
    public void TryClampRegion(int x, int y, int w, int h, bool expected)
    {
        var r = R(x, y, w, h);
        Assert.Equal(expected, RegionGeometry.TryClampRegion(r, 100, 100));
        if (expected)
        {
            Assert.True(r.X + r.W <= 100);
            Assert.True(r.Y + r.H <= 100);
        }
    }

    [Fact]
    public void ValidateTimeRange_EndBeforeStart_Rejected()
    {
        Assert.NotEmpty(RegionGeometry.ValidateTimeRange(5, 5, 60));
        Assert.NotEmpty(RegionGeometry.ValidateTimeRange(10, 5, 60));
        Assert.NotEmpty(RegionGeometry.ValidateTimeRange(0, 61, 60));
        Assert.Equal("", RegionGeometry.ValidateTimeRange(1, 5, 60));
    }
}
