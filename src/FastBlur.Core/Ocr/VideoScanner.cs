using System.Diagnostics;
using FastBlur.Core.Detect;
using FastBlur.Core.Models;

namespace FastBlur.Core.Ocr;

/// <summary>
/// Frame extraction via ffmpeg + tesseract OCR.
/// Port of BlurCut's ocr.go scanVideo / ocrPreviewFrame.
/// Frames are streamed to a temp dir, never held in RAM — a 30-minute
/// video scans the same way a 30-second one does.
/// </summary>
public sealed class VideoScanner
{
    private readonly ToolsInfo _tools;

    public VideoScanner(ToolsInfo tools) => _tools = tools;

    /// <summary>
    /// Extracts frames every <paramref name="interval"/> seconds, OCRs each,
    /// and returns merged detections. Reports progress after each frame so a
    /// long scan shows real progress instead of sitting at 0% for minutes.
    /// </summary>
    public async Task<List<Detection>> ScanAsync(
        string videoPath, double interval, IReadOnlyList<string> keywords,
        IReadOnlyDictionary<string, bool> patterns, VideoInfo probe,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        if (!_tools.FFmpegOk) throw new InvalidOperationException("ffmpeg not found.");
        if (!_tools.TesseractOk) throw new InvalidOperationException("tesseract not found.");

        var tmp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fastblur-scan-" + Guid.NewGuid().ToString("N")[..8]));
        try
        {
            double f = TesseractRunner.OcrScaleFactor(probe.Width);
            var vf = $"fps=1/{interval.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (f > 1)
                vf += $",scale=iw*{f.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:ih*{f.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";

            await RunFfmpegAsync(new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-i", videoPath, "-vf", vf, "-q:v", "4",
                Path.Combine(tmp.FullName, "frame_%04d.jpg"),
            }, ct);

            var frames = Directory.GetFiles(tmp.FullName, "frame_*.jpg").OrderBy(x => x).ToArray();

            var raw = new List<Detection>();
            int ocrErrors = 0;
            Exception? lastError = null;
            for (int i = 0; i < frames.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Frame 1 is at t=0, frame n at t=(n-1)*interval.
                double t = i * interval;
                try
                {
                    var words = await TesseractRunner.RunAsync(_tools.Tesseract, _tools.TessData, frames[i], ct);
                    if (words.Count > 0)
                    {
                        // Group in upscaled space (gaps are measured there),
                        // then convert the merged boxes back to original pixels.
                        var groups = TesseractRunner.ScaleWordsToOriginal(WordGrouper.GroupWords(words, f), f);
                        var frameRegions = new List<Region>();
                        foreach (var g in groups)
                        {
                            var (label, text) = SensitiveTextClassifier.FindSensitive(new[] { g }, keywords, patterns);
                            if (label.Length == 0) continue;
                            var r = new Region
                            {
                                Label = label, Text = text,
                                X = g.X, Y = g.Y, W = g.W, H = g.H,
                                Start = t, End = t + interval, Enabled = true, Source = "auto",
                            };
                            RegionGeometry.PadRegion(r, 8, probe.Width, probe.Height);
                            RegionGeometry.PixelToRelative(r, probe.Width, probe.Height);
                            frameRegions.Add(r);
                        }
                        foreach (var r in RegionGeometry.MergeBoxes(frameRegions))
                            raw.Add(new Detection
                            {
                                Label = r.Label, Text = r.Text, X = r.X, Y = r.Y, W = r.W, H = r.H,
                                Rx = r.Rx, Ry = r.Ry, Rw = r.Rw, Rh = r.Rh,
                                Start = r.Start, End = r.End, Enabled = r.Enabled, Source = r.Source, Frames = 1,
                            });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad frame must not kill the whole scan.
                    ocrErrors++;
                    lastError = ex;
                }
                progress?.Report((i + 1, frames.Length));
            }

            var merged = RegionGeometry.MergeTimeRanges(raw);
            for (int i = 0; i < merged.Count; i++)
                merged[i].Id = $"det-{i + 1}";

            // If OCR failed on every frame, say so — "0 regions found"
            // would be a lie (e.g. tesseract cannot load its language data).
            if (frames.Length > 0 && ocrErrors == frames.Length)
                throw new InvalidOperationException(
                    $"tesseract failed on all {frames.Length} frames (tessdata='{_tools.TessData}'): {lastError?.Message}");

            return merged;
        }
        finally
        {
            try { tmp.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Answers "What does the scan see here?": extracts ONE frame at time
    /// <paramref name="t"/>, OCRs it with the same upscale the scan uses,
    /// and returns the raw words (reading order, capped at 40) in
    /// original-pixel coordinates. Nothing is stored.
    /// </summary>
    public async Task<List<OcrWord>> OcrPreviewFrameAsync(
        string videoPath, double t, VideoInfo probe, CancellationToken ct = default)
    {
        if (!_tools.FFmpegOk) throw new InvalidOperationException("ffmpeg not found.");
        if (!_tools.TesseractOk) throw new InvalidOperationException("tesseract not found.");

        var tmp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fastblur-ocr-" + Guid.NewGuid().ToString("N")[..8]));
        try
        {
            var frame = Path.Combine(tmp.FullName, "frame.jpg");
            double f = TesseractRunner.OcrScaleFactor(probe.Width);
            var vf = "format=yuv420p";
            if (f > 1)
                vf += $",scale=iw*{f.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:ih*{f.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";

            await RunFfmpegAsync(new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-i", videoPath, "-ss", t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-frames:v", "1", "-vf", vf, "-q:v", "4", frame,
            }, ct);

            var words = await TesseractRunner.RunAsync(_tools.Tesseract, _tools.TessData, frame, ct);
            words = TesseractRunner.ScaleWordsToOriginal(words, f);
            var ordered = words.OrderBy(w => w.Y).ThenBy(w => w.X).Take(40).ToList();
            return ordered;
        }
        finally
        {
            try { tmp.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task RunFfmpegAsync(string[] args, CancellationToken ct)
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
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg frame extract failed: {stderr.Trim()}");
    }
}
