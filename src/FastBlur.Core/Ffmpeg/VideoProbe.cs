using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastBlur.Core.Models;

namespace FastBlur.Core.Ffmpeg;

/// <summary>Probes a video file (ffprobe JSON, falling back to ffmpeg -i stderr).</summary>
public static class VideoProbe
{
    /// <summary>
    /// Probes via ffprobe, falling back to <c>ffmpeg -i</c>.
    /// Format is normalised to the file extension: ffprobe reports
    /// "mov,mp4,..." for both .mp4 and .mov, and only the extension tells
    /// them apart for playback decisions.
    /// </summary>
    public static async Task<VideoInfo> ProbeAsync(ToolsInfo tools, string path, CancellationToken ct = default)
    {
        if (!tools.FFmpegOk && tools.FFprobe.Length == 0)
            throw new InvalidOperationException($"ffmpeg not found — searched: {tools.SearchedSummary("ffmpeg")}");

        if (tools.FFprobe.Length > 0)
        {
            try
            {
                var psi = new ProcessStartInfo(tools.FFprobe)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path })
                    psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = await proc.StandardOutput.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode == 0)
                    {
                        var parsed = ParseFFprobeJson(output);
                        if (parsed.Duration > 0 && parsed.Width > 0)
                            return NormalizeProbe(path, parsed);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to the ffmpeg -i fallback.
            }
        }

        if (!tools.FFmpegOk)
            throw new InvalidOperationException($"ffmpeg not found — searched: {tools.SearchedSummary("ffmpeg")}");

        var psi2 = new ProcessStartInfo(tools.FFmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi2.ArgumentList.Add("-hide_banner");
        psi2.ArgumentList.Add("-i");
        psi2.ArgumentList.Add(path);
        using var proc2 = Process.Start(psi2);
        if (proc2 == null) throw new InvalidOperationException("Could not start ffmpeg.");
        var combined = await proc2.StandardOutput.ReadToEndAsync(ct)
            + await proc2.StandardError.ReadToEndAsync(ct);
        await proc2.WaitForExitAsync(ct);
        var info = ParseFFmpegStderr(combined);
        if (info.Duration == 0)
            throw new InvalidOperationException($"could not probe video: {FirstLine(combined)}");
        return NormalizeProbe(path, info);
    }

    public static VideoInfo NormalizeProbe(string path, VideoInfo info)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0) info.Format = ext;
        return info;
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return (i >= 0 ? s[..i] : s).Trim();
    }

    /// <summary>Pulls duration/size/fps/codec/format out of ffprobe JSON output.</summary>
    public static VideoInfo ParseFFprobeJson(string json)
    {
        var info = new VideoInfo();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.String
                && double.TryParse(dur.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                info.Duration = d;
            if (format.TryGetProperty("format_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                info.Format = fn.GetString()!.Split(',')[0];
        }
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var ct) && ct.GetString() != "video") continue;
                if (s.TryGetProperty("width", out var w)) info.Width = w.GetInt32();
                if (s.TryGetProperty("height", out var h)) info.Height = h.GetInt32();
                if (s.TryGetProperty("codec_name", out var cn)) info.Codec = cn.GetString() ?? "";
                if (s.TryGetProperty("r_frame_rate", out var fps) && fps.ValueKind == JsonValueKind.String)
                {
                    var parts = fps.GetString()!.Split('/');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var num)
                        && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var den)
                        && den != 0)
                        info.Fps = num / den;
                }
                break;
            }
        }
        return info;
    }

    /// <summary>
    /// Pulls the same fields out of <c>ffmpeg -i</c> stderr:
    /// "Input #0, matroska, from ..." gives the container,
    /// "Stream #0:0: Video: h264 ..." gives the codec and size.
    /// </summary>
    public static VideoInfo ParseFFmpegStderr(string s)
    {
        var info = new VideoInfo();
        var m = Regex.Match(s, @"Duration: (\d+):(\d+):([\d.]+)");
        if (m.Success)
        {
            double h = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double mi = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double sec = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            info.Duration = h * 3600 + mi * 60 + sec;
        }
        m = Regex.Match(s, @"Input #\d+, ([a-z0-9_,]+), from");
        if (m.Success) info.Format = m.Groups[1].Value.Split(',')[0];
        m = Regex.Match(s, @"Stream.*Video: ([a-z0-9_]+)");
        if (m.Success) info.Codec = m.Groups[1].Value;
        m = Regex.Match(s, @"Stream.*Video:.* (\d+)x(\d+)");
        if (m.Success)
        {
            info.Width = int.Parse(m.Groups[1].Value);
            info.Height = int.Parse(m.Groups[2].Value);
        }
        m = Regex.Match(s, @"(\d+(?:\.\d+)?) fps");
        if (m.Success) info.Fps = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return info;
    }
}
