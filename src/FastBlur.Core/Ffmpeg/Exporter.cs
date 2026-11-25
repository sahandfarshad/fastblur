using System.Diagnostics;
using System.Text.RegularExpressions;
using FastBlur.Core.Models;

namespace FastBlur.Core.Ffmpeg;

/// <summary>
/// Runs ffmpeg with the blur filter, makes playable previews, and verifies
/// exports. Port of BlurCut's ffmpeg.go exportVideo / verifyExport /
/// makePreview / extractErrorLines. Streams the file; never loads it in RAM.
/// </summary>
public sealed class Exporter
{
    private readonly ToolsInfo _tools;

    public Exporter(ToolsInfo tools) => _tools = tools;

    // Matches the ffmpeg stderr lines that actually say what went wrong —
    // as opposed to progress lines ("frame= ... fps=...") and the
    // "Input #0, ..." header.
    private static readonly Regex ErrorLineRegex = new(
        @"error|invalid|not divisible|could not|failed|no such|unsupported",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Picks the meaningful lines out of ffmpeg stderr: every error-like
    /// line plus the last 10 lines, capped at ~1500 chars, so the user sees
    /// "width not divisible by 2" instead of a useless "Input #0, ..."
    /// first line or a bare "Conversion failed!".
    /// </summary>
    public static string ExtractErrorLines(string stderr)
    {
        var lines = stderr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return "";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        void Add(string ln)
        {
            if (seen.Add(ln)) kept.Add(ln);
        }
        foreach (var ln in lines)
            if (ErrorLineRegex.IsMatch(ln)) Add(ln);

        int start = Math.Max(0, lines.Count - 10);
        foreach (var ln in lines.Skip(start))
        {
            // The tail is context, not noise: skip pure progress lines
            // ("frame= ... fps=..."), the "Input #0, ..." header and the
            // version banner — they say nothing about the failure.
            if (ln.StartsWith("frame=", StringComparison.Ordinal)
                || ln.StartsWith("Input #", StringComparison.Ordinal)
                || ln.StartsWith("ffmpeg version", StringComparison.Ordinal))
                continue;
            Add(ln);
        }
        var s = string.Join(" | ", kept);
        return s.Length > 1500 ? s[..1500] : s;
    }

    /// <summary>
    /// Runs ffmpeg with the blur filter, reporting progress 0-1 as it parses
    /// time= from stderr. Fallbacks, in order: h264_nvenc (if listed) →
    /// libx264 with audio copy → libx264 with audio re-encoded to AAC
    /// (mkv audio like Vorbis/Opus/PCM cannot be copied into mp4, so a
    /// copy-only export would fail outright). Returns the encoder path that
    /// succeeded ("h264_nvenc", "libx264+copy" or "libx264+aac") so the UI
    /// can say how the file was made.
    /// </summary>
    public async Task<string> ExportAsync(
        string input, string output, IReadOnlyList<Region> regions, int blurStrength,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!_tools.FFmpegOk) throw new InvalidOperationException("ffmpeg not found.");
        var info = await VideoProbe.ProbeAsync(_tools, input, ct);
        var fc = FilterBuilder.BuildFilterComplex(regions, blurStrength, info.Width, info.Height);
        if (fc.Length == 0) throw new InvalidOperationException("no enabled blur regions");

        async Task RunAsync(string[] encArgs, string audio)
        {
            var args = new List<string>
            {
                "-hide_banner", "-y", "-i", input,
                "-filter_complex", fc, "-map", "[vout]", "-map", "0:a?",
            };
            args.AddRange(encArgs);
            args.AddRange(new[] { "-c:a", audio, "-movflags", "+faststart", output });
            await RunFfmpegWithProgressAsync(args, info.Duration, progress, ct);
        }

        var x264 = new[] { "-c:v", "libx264", "-crf", "18", "-preset", "veryfast" };
        if (_tools.HasNvenc)
        {
            try
            {
                await RunAsync(new[] { "-c:v", "h264_nvenc", "-cq", "23" }, "copy");
                return "h264_nvenc";
            }
            catch
            {
                // NVENC listed but no usable GPU — fall through to libx264.
                progress?.Report(0);
            }
        }
        try
        {
            await RunAsync(x264, "copy");
            return "libx264+copy";
        }
        catch
        {
            progress?.Report(0);
        }
        // Audio copy failed (e.g. Vorbis/Opus in an .mkv cannot go into mp4)
        // — retry the same encode with the audio re-encoded to AAC.
        await RunAsync(x264, "aac");
        return "libx264+aac";
    }

    private async Task RunFfmpegWithProgressAsync(
        IReadOnlyList<string> args, double duration, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_tools.FFmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");

        var errorLines = new List<string>();
        var tail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync(ct) is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (ErrorLineRegex.IsMatch(trimmed)) errorLines.Add(trimmed);
                tail.Enqueue(trimmed);
                while (tail.Count > 10) tail.Dequeue();
                if (FilterBuilder.TryParseProgressTime(line, out var secs) && duration > 0)
                    progress?.Report(Math.Min(1, secs / duration));
            }
        }, ct);

        await proc.WaitForExitAsync(ct);
        await stderrTask;
        if (proc.ExitCode != 0)
        {
            var detail = ExtractErrorLines(string.Join("\n", errorLines.Concat(tail)));
            throw new InvalidOperationException($"ffmpeg export failed (exit {proc.ExitCode}): {detail}");
        }
    }

    /// <summary>
    /// Converts input into a playable mp4 preview at output. h264 video with
    /// EVEN dimensions is remuxed (-c:v copy, no quality loss, near-instant);
    /// anything else is re-encoded to libx264 CRF 20, downscaled to at most
    /// 1280 wide. Audio is copied when possible, else re-encoded to AAC.
    /// The preview is only for playback in the UI — scan/export keep using
    /// the original file.
    /// </summary>
    public async Task MakePreviewAsync(string input, string output, VideoInfo info, CancellationToken ct = default)
    {
        if (!_tools.FFmpegOk) throw new InvalidOperationException("ffmpeg not found.");

        async Task RunAsync(string[] args)
        {
            var psi = new ProcessStartInfo(_tools.FFmpeg)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start ffmpeg.");
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"preview failed: {ExtractErrorLines(stderr)}");
        }

        if (FilterBuilder.CanRemuxPreview(info))
        {
            try
            {
                await RunAsync(new[]
                {
                    "-hide_banner", "-y", "-i", input,
                    "-map", "0:v:0", "-map", "0:a?", "-c", "copy",
                    "-movflags", "+faststart", output,
                });
                return;
            }
            catch
            {
                // Copy failed (e.g. audio codec not allowed in mp4) — retry
                // with the audio re-encoded to AAC, video still copied.
                await RunAsync(new[]
                {
                    "-hide_banner", "-y", "-i", input,
                    "-map", "0:v:0", "-map", "0:a?", "-c:v", "copy", "-c:a", "aac",
                    "-movflags", "+faststart", output,
                });
                return;
            }
        }

        await RunAsync(new[]
        {
            "-hide_banner", "-y", "-i", input,
            "-map", "0:v:0", "-map", "0:a?",
            "-vf", FilterBuilder.PreviewScaleFilter(),
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast",
            "-c:a", "aac", "-movflags", "+faststart", output,
        });
    }

    /// <summary>
    /// Checks that an export actually produced a playable mp4: the file must
    /// exist, be non-empty, and probe to a video stream with a duration.
    /// ffmpeg can exit 0 yet leave a broken or empty file behind, so the UI
    /// must never offer such a file as "done" — a bad export is reported as
    /// an error with the reason instead.
    /// </summary>
    public async Task VerifyExportAsync(string path, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new InvalidOperationException($"export file missing: {path}");
        if (fi.Length == 0) throw new InvalidOperationException("export file is empty (0 bytes)");
        var info = await VideoProbe.ProbeAsync(_tools, path, ct);
        if (info.Width <= 0 || info.Duration <= 0)
            throw new InvalidOperationException(
                $"export file has no video stream (width={info.Width} duration={info.Duration:F1})");
    }
}
