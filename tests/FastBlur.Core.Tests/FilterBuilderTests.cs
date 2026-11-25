using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Models;

namespace FastBlur.Core.Tests;

public sealed class FilterBuilderTests
{
    private static Region R(int x, int y, int w, int h, double start = 1, double end = 5, bool enabled = true) =>
        new() { X = x, Y = y, W = w, H = h, Start = start, End = end, Enabled = enabled };

    [Fact]
    public void NoEnabledRegions_ReturnsEmpty()
    {
        Assert.Equal("", FilterBuilder.BuildFilterComplex(Array.Empty<Region>(), 20, 1920, 1080));
        Assert.Equal("", FilterBuilder.BuildFilterComplex(new[] { R(0, 0, 10, 10, enabled: false) }, 20, 1920, 1080));
        Assert.Equal("", FilterBuilder.BuildFilterComplex(new[] { R(0, 0, 10, 10, start: 5, end: 5) }, 20, 1920, 1080));
    }

    [Fact]
    public void OneRegion_BuildsSplitCropBlurOverlayChain()
    {
        var fc = FilterBuilder.BuildFilterComplex(new[] { R(100, 50, 200, 40, 1, 5) }, 20, 1920, 1080);
        Assert.Contains("[0:v]split=2[s0][s1];", fc);
        Assert.Contains("[s1]crop=200:40:100:50,boxblur=", fc);
        Assert.Contains("[s0][b0]overlay=100:50:enable='between(t,1.00,5.00)'[o0];", fc);
        // Output is forced to even dimensions, even though 1920x1080 already is.
        Assert.EndsWith("[o0]scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p[vout]", fc);
    }

    [Fact]
    public void SmallRegion_BlurRadius_ClampedBelowHalfSmallestSide()
    {
        // A 100x20 text line with strength 20: luma radius must be < 10,
        // chroma radius < 5 — otherwise ffmpeg fails the whole export (-22).
        var fc = FilterBuilder.BuildFilterComplex(new[] { R(10, 10, 100, 20, 1, 5) }, 20, 1920, 1080);
        Assert.Contains("boxblur=luma_radius=9:luma_power=1:chroma_radius=4:chroma_power=1", fc);
    }

    [Fact]
    public void LargeRegion_UsesFullStrength()
    {
        var fc = FilterBuilder.BuildFilterComplex(new[] { R(10, 10, 400, 300, 1, 5) }, 20, 1920, 1080);
        Assert.Contains("boxblur=luma_radius=20:luma_power=1:chroma_radius=10:chroma_power=1", fc);
    }

    [Fact]
    public void OutOfFrameRegion_IsClampedOrDropped()
    {
        // Partially outside: clamped into the frame.
        var fc = FilterBuilder.BuildFilterComplex(new[] { R(1900, 1000, 100, 100, 1, 5) }, 20, 1920, 1080);
        Assert.Contains("crop=20:80:1900:1000", fc);
        // Fully outside: dropped, so no filter at all.
        Assert.Equal("", FilterBuilder.BuildFilterComplex(new[] { R(5000, 5000, 100, 100, 1, 5) }, 20, 1920, 1080));
    }

    [Fact]
    public void MultipleRegions_ChainOverlaysInOrder()
    {
        var fc = FilterBuilder.BuildFilterComplex(
            new[] { R(0, 0, 50, 50, 1, 2), R(100, 100, 50, 50, 3, 4) }, 20, 1920, 1080);
        Assert.Contains("[0:v]split=3[s0][s1][s2];", fc);
        Assert.Contains("[o0][b1]overlay=100:100:enable='between(t,3.00,4.00)'[o1];", fc);
        Assert.EndsWith("[o1]scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p[vout]", fc);
    }

    [Fact]
    public void CanRemuxPreview_OnlyH264EvenDims()
    {
        Assert.True(FilterBuilder.CanRemuxPreview(new VideoInfo { Codec = "h264", Width = 1920, Height = 1080 }));
        Assert.False(FilterBuilder.CanRemuxPreview(new VideoInfo { Codec = "h264", Width = 1279, Height = 719 }));
        Assert.False(FilterBuilder.CanRemuxPreview(new VideoInfo { Codec = "hevc", Width = 1920, Height = 1080 }));
        Assert.False(FilterBuilder.CanRemuxPreview(new VideoInfo { Codec = "vp9", Width = 1920, Height = 1080 }));
    }

    [Fact]
    public void PreviewScaleFilter_ForcesEvenDims_CapsAt1280()
    {
        Assert.Equal("scale='trunc(min(1280,iw)/2)*2':-2,format=yuv420p", FilterBuilder.PreviewScaleFilter());
    }

    [Theory]
    [InlineData("frame=  100 fps=25 time=00:00:04.00 bitrate= 100kbits/s", 4.0)]
    [InlineData("time=01:02:03.50", 3723.5)]
    public void TryParseProgressTime(string line, double expected)
    {
        Assert.True(FilterBuilder.TryParseProgressTime(line, out var seconds));
        Assert.Equal(expected, seconds, precision: 3);
    }

    [Fact]
    public void TryParseProgressTime_NoTime_ReturnsFalse()
    {
        Assert.False(FilterBuilder.TryParseProgressTime("frame= 10 fps=25", out _));
    }
}

public sealed class ExporterErrorLineTests
{
    [Fact]
    public void ExtractErrorLines_PicksRealErrors_SkipsNoise()
    {
        var stderr = """
            ffmpeg version 8.1.2 Copyright (c) 2000-2025
            Input #0, matroska, from 'test.mkv':
            frame=  100 fps=25 time=00:00:04.00 bitrate= 100kbits/s
            [libx264 @ 0x123] width not divisible by 2 (1279x719)
            Conversion failed!
            """;
        var result = Exporter.ExtractErrorLines(stderr);
        Assert.Contains("width not divisible by 2", result);
        Assert.Contains("Conversion failed!", result);
        Assert.DoesNotContain("frame=", result);
        Assert.DoesNotContain("Input #0", result);
        Assert.DoesNotContain("ffmpeg version", result);
    }

    [Fact]
    public void ExtractErrorLines_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", Exporter.ExtractErrorLines(""));
        Assert.Equal("", Exporter.ExtractErrorLines("   \n\n"));
    }

    [Fact]
    public void ExtractErrorLines_Dedupes_AndCapsLength()
    {
        var stderr = string.Join("\n", Enumerable.Repeat("Error: something failed", 200));
        var result = Exporter.ExtractErrorLines(stderr);
        Assert.Single(result.Split(" | "));
        Assert.True(result.Length <= 1500);
    }

    [Fact]
    public void ExtractErrorLines_KeepsTailContext()
    {
        var stderr = "frame= 1 fps=1 time=00:00:00.04\nsome odd last line\n";
        var result = Exporter.ExtractErrorLines(stderr);
        Assert.Contains("some odd last line", result);
    }
}

public sealed class PlaybackSupportTests
{
    [Theory]
    [InlineData("mp4", "h264", true)]
    [InlineData("mp4", "hevc", false)]
    [InlineData("mkv", "h264", false)]
    [InlineData("mov", "h264", false)]
    [InlineData("webm", "vp9", false)]
    public void IsDirectlyPlayable(string format, string codec, bool expected)
    {
        Assert.Equal(expected, PlaybackSupport.IsDirectlyPlayable(
            new VideoInfo { Format = format, Codec = codec, Width = 1920, Height = 1080 }));
    }

    [Fact]
    public void OddDimensions_AreFine_ForAPlayer()
    {
        // Unlike libx264, a player has no even-dimension requirement.
        Assert.True(PlaybackSupport.IsDirectlyPlayable(
            new VideoInfo { Format = "mp4", Codec = "h264", Width = 1279, Height = 719 }));
    }
}
