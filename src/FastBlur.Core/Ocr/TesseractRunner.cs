using System.Diagnostics;
using FastBlur.Core.Detect;
using FastBlur.Core.Models;

namespace FastBlur.Core.Ocr;

/// <summary>
/// Runs tesseract on image files and parses its TSV output.
/// Port of BlurCut's ocr.go runTesseract / parseTSV / ocrScaleFactor.
/// </summary>
public static class TesseractRunner
{
    /// <summary>
    /// Parses <c>tesseract stdout tsv</c> output into words.
    /// Columns: level page block par line word left top width height conf text.
    /// Only level-5 (word) rows with non-empty text and confidence &gt;= 20.
    /// Tesseract 5 prints confidence as a float, e.g. "95.000000".
    /// </summary>
    public static List<OcrWord> ParseTsv(string tsv)
    {
        var words = new List<OcrWord>();
        foreach (var line in tsv.Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 12 || parts[0] != "5") continue;
            var text = parts[11].Trim();
            if (text.Length == 0) continue;
            if (!int.TryParse(parts[6], out var left)) continue;
            if (!int.TryParse(parts[7], out var top)) continue;
            if (!int.TryParse(parts[8], out var w)) continue;
            if (!int.TryParse(parts[9], out var h)) continue;
            if (!double.TryParse(parts[10], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var confF)) continue;
            int conf = (int)confF;
            if (conf < 20) continue;
            words.Add(new OcrWord { Text = text, X = left, Y = top, W = w, H = h, Conf = conf });
        }
        return words;
    }

    /// <summary>
    /// How much frames are upscaled before OCR. Small screen text (browser
    /// chrome, terminal, 13px at 1080p) misreads at 1x — tesseract reads
    /// "password:" as "sword:" with high confidence, so no pattern matches
    /// and the scan finds nothing — but reads cleanly at 2x. 4K frames are
    /// already big enough, and the upscaled long side is capped at ~3840 so
    /// a 2560-wide frame does not become 5120 wide.
    /// </summary>
    public static double OcrScaleFactor(int frameW)
    {
        if (frameW <= 0 || frameW >= 2560) return 1;
        double f = 3840.0 / frameW;
        return f > 2 ? 2 : f;
    }

    /// <summary>
    /// Converts OCR word coordinates from upscaled frame space back to
    /// original-pixel space (divide by the upscale factor, rounded).
    /// </summary>
    public static List<OcrWord> ScaleWordsToOriginal(IReadOnlyList<OcrWord> words, double factor)
    {
        if (factor == 1) return words.ToList();
        return words.Select(w => new OcrWord
        {
            Text = w.Text,
            X = (int)Math.Round(w.X / factor),
            Y = (int)Math.Round(w.Y / factor),
            W = (int)Math.Round(w.W / factor),
            H = (int)Math.Round(w.H / factor),
            Conf = w.Conf,
        }).ToList();
    }

    /// <summary>
    /// Runs tesseract on one image file and returns OCR words.
    /// TSV output is requested with <c>-c tessedit_create_tsv=1</c> instead
    /// of the "tsv" config file, because a bundled/minimal tessdata folder
    /// has no config files — with the plain "tsv" argument tesseract
    /// silently falls back to plain text and every word is lost.
    /// <paramref name="tessData"/> is the tessdata dir itself, used as
    /// TESSDATA_PREFIX ("" = tesseract's default).
    /// </summary>
    public static async Task<List<OcrWord>> RunAsync(
        string tesseractPath, string tessData, string imagePath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(tesseractPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(imagePath);
        psi.ArgumentList.Add("stdout");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("tessedit_create_tsv=1");
        if (tessData.Length > 0)
            psi.Environment["TESSDATA_PREFIX"] = tessData;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start tesseract.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"tesseract failed (exit {proc.ExitCode}): {stderr.Trim()}");
        return ParseTsv(stdout);
    }
}
