using System.Diagnostics;
using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Models;

namespace FastBlur.Core.Tests;

/// <summary>
/// End-to-end tests against a real ffmpeg (and ffprobe) install.
/// These run in CI after the ffmpeg install step, and locally wherever
/// ffmpeg is on PATH. They exercise probe → filter → export → verify
/// for real, including the odd-dimension case that broke libx264 in the
/// Go version (v0.2.2).
/// </summary>
public sealed class IntegrationTests : IDisposable
{
    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "fastblur-inttest-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static readonly bool FfmpegAvailable =
        FindOnPath("ffmpeg") != null && FindOnPath("ffprobe") != null;

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private ToolsInfo Tools() => new()
    {
        FFmpeg = FindOnPath("ffmpeg") ?? "",
        FFprobe = FindOnPath("ffprobe") ?? "",
    };

    private static async Task RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {stderr}");
    }

    /// <summary>Generates a short test video (colour bars + a tone) with ffmpeg.</summary>
    private async Task<string> MakeVideoAsync(string name, int w, int h, double seconds = 3)
    {
        var path = Path.Combine(_tmp, name);
        // libx264 refuses odd dimensions ("width not divisible by 2"), so
        // odd-sized fixtures are encoded with mpeg4, which allows them —
        // the odd-dimension export test needs a real odd-sized source.
        var codec = (w % 2 == 0 && h % 2 == 0) ? "libx264" : "mpeg4";
        await RunAsync(FindOnPath("ffmpeg")!,
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"testsrc=size={w}x{h}:rate=30:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-c:v", codec, "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path);
        return path;
    }

    [Fact]
    public async Task Probe_RealVideo_ReturnsDimensionsAndDuration()
    {
        if (!FfmpegAvailable) return; // skipped where ffmpeg is absent
        var video = await MakeVideoAsync("probe.mp4", 1280, 720);
        var info = await VideoProbe.ProbeAsync(Tools(), video);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal("h264", info.Codec);
        Assert.True(info.Duration > 2.5 && info.Duration < 4);
    }

    [Fact]
    public async Task Export_OddDimensionVideo_Succeeds()
    {
        // 1279x719 broke libx264 with -22 "width not divisible by 2" in the
        // Go version until the export forced even output dimensions.
        if (!FfmpegAvailable) return;
        var video = await MakeVideoAsync("odd.mp4", 1279, 719);
        var regions = new[]
        {
            new Region { X = 100, Y = 50, W = 200, H = 40, Start = 0, End = 2, Enabled = true },
        };
        var outPath = Path.Combine(_tmp, "odd-blurred.mp4");
        var exporter = new Exporter(Tools());
        var enc = await exporter.ExportAsync(video, outPath, regions, 20);
        Assert.Contains("libx264", enc);
        await exporter.VerifyExportAsync(outPath);
        var info = await VideoProbe.ProbeAsync(Tools(), outPath);
        Assert.Equal(1278, info.Width); // forced even: at most 1px smaller
        Assert.Equal(718, info.Height);
        Assert.True(new FileInfo(outPath).Length > 0);
    }

    [Fact]
    public async Task Export_SmallRegion_RadiusClamp_DoesNotFail()
    {
        // A 100x20 text-line region with strength 20 failed whole exports
        // in the Go version until radii were clamped per region.
        if (!FfmpegAvailable) return;
        var video = await MakeVideoAsync("small.mp4", 640, 360);
        var regions = new[]
        {
            new Region { X = 10, Y = 10, W = 100, H = 20, Start = 0, End = 2, Enabled = true },
        };
        var outPath = Path.Combine(_tmp, "small-blurred.mp4");
        var exporter = new Exporter(Tools());
        await exporter.ExportAsync(video, outPath, regions, 20);
        await exporter.VerifyExportAsync(outPath);
    }

    [Fact]
    public async Task Export_MkvWithVorbisAudio_ExportsValidMp4()
    {
        // Modern ffmpeg (8.x) accepts Vorbis in MP4, so the copy path
        // succeeds — the export must still come out a verified, valid mp4.
        // The copy→AAC fallback is covered separately with a fake ffmpeg
        // that refuses the copy (Export_AudioCopyFailure_FallsBackToAac).
        if (!FfmpegAvailable) return;
        var mkv = Path.Combine(_tmp, "audio.mkv");
        await RunAsync(FindOnPath("ffmpeg")!,
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", "testsrc=size=640x360:rate=30:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "libvorbis", "-shortest", mkv);
        var regions = new[]
        {
            new Region { X = 10, Y = 10, W = 100, H = 40, Start = 0, End = 2, Enabled = true },
        };
        var outPath = Path.Combine(_tmp, "audio-blurred.mp4");
        var exporter = new Exporter(Tools());
        var enc = await exporter.ExportAsync(mkv, outPath, regions, 20);
        Assert.StartsWith("libx264", enc);
        await exporter.VerifyExportAsync(outPath);
    }

    [Fact]
    public async Task Export_AudioCopyFailure_FallsBackToAac()
    {
        // A fake ffmpeg that refuses any "-c:a copy" run: the export must
        // fall back to re-encoding the audio to AAC instead of failing.
        // Unix-only: the fake is a shell script.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!FfmpegAvailable) return;
        var video = await MakeVideoAsync("fallback.mp4", 640, 360);
        var fake = Path.Combine(_tmp, "fake-ffmpeg");
        await File.WriteAllTextAsync(fake,
            "#!/bin/sh\n" +
            "for a in \"$@\"; do\n" +
            "  if [ \"$a\" = \"copy\" ]; then echo \"mock: audio copy not supported\" >&2; exit 1; fi\n" +
            "done\n" +
            "exec /usr/bin/ffmpeg \"$@\"\n");
        // Make it executable.
        File.SetUnixFileMode(fake,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tools = new ToolsInfo { FFmpeg = fake, FFprobe = FindOnPath("ffprobe") ?? "" };
        var regions = new[]
        {
            new Region { X = 10, Y = 10, W = 100, H = 40, Start = 0, End = 2, Enabled = true },
        };
        var outPath = Path.Combine(_tmp, "fallback-blurred.mp4");
        var exporter = new Exporter(tools);
        var enc = await exporter.ExportAsync(video, outPath, regions, 20);
        Assert.Equal("libx264+aac", enc);
        await exporter.VerifyExportAsync(outPath);
    }

    [Fact]
    public async Task MakePreview_Mkv_ProducesPlayableMp4()
    {
        if (!FfmpegAvailable) return;
        var mkv = Path.Combine(_tmp, "prev.mkv");
        await RunAsync(FindOnPath("ffmpeg")!,
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=30:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", mkv);
        var info = await VideoProbe.ProbeAsync(Tools(), mkv);
        Assert.False(PlaybackSupport.IsDirectlyPlayable(info));
        var preview = Path.Combine(_tmp, "prev_preview.mp4");
        await new Exporter(Tools()).MakePreviewAsync(mkv, preview, info);
        var previewInfo = await VideoProbe.ProbeAsync(Tools(), preview);
        Assert.Equal("mp4", previewInfo.Format);
        Assert.True(PlaybackSupport.IsDirectlyPlayable(previewInfo));
    }

    [Fact]
    public async Task VerifyExport_EmptyFile_Fails()
    {
        var empty = Path.Combine(_tmp, "empty.mp4");
        await File.WriteAllTextAsync(empty, "");
        var exporter = new Exporter(Tools());
        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.VerifyExportAsync(empty));
    }
}
