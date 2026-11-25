using System.Text.Json;
using FastBlur.Core.Detect;
using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Jobs;
using FastBlur.Core.Models;
using FastBlur.Core.Ocr;

namespace FastBlur.Core.Cli;

/// <summary>
/// Headless CLI, same surface as the Go version:
///   fastblur scan &lt;video&gt; --out detections.json
///   fastblur blur &lt;video&gt; --regions regions.json --out out.mp4
///   fastblur --help | --version
/// Returns a process exit code. Used by the WPF App when started with args.
/// </summary>
public static class CliRunner
{
    public const string Version = "0.1.0";

    public static void PrintUsage(TextWriter w)
    {
        w.Write($"""
            FastBlur {Version} — auto-blur sensitive info in videos (offline)

            Usage:
              fastblur                    open the FastBlur window (Windows)
              fastblur scan <video> --out detections.json
              fastblur blur <video> --regions regions.json --out out.mp4
              fastblur --help
              fastblur --version

            Double-clicking FastBlur.exe opens its own window — no browser tab,
            no URL to type. ffmpeg + tesseract are bundled in the Windows zip and
            are found next to FastBlur.exe (or in tools/); the Tools section in the
            UI can also download them. The blurred file keeps the original audio
            and quality. No internet, no API.
            """);
    }

    public static async Task<int> RunAsync(
        string[] args, string dataDir, AppConfig cfg, ToolsInfo tools, JobStore store)
    {
        switch (args[0])
        {
            case "--help":
            case "-h":
            case "help":
                PrintUsage(Console.Out);
                return 0;
            case "--version":
            case "-v":
            case "version":
                Console.WriteLine("fastblur " + Version);
                return 0;
            case "scan":
                return await RunScanAsync(args[1..], cfg, tools);
            case "blur":
                return await RunBlurAsync(args[1..], cfg, tools);
            default:
                Console.Error.WriteLine("fastblur: unknown command '" + args[0] + "'");
                PrintUsage(Console.Error);
                return 1;
        }
    }

    private static (List<string> Positionals, Dictionary<string, string> Flags) ParseArgs(string[] args)
    {
        var positionals = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = a.IndexOf('=');
                if (eq >= 0) flags[a[..eq]] = a[(eq + 1)..];
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    flags[a] = args[++i];
                else flags[a] = "";
            }
            else positionals.Add(a);
        }
        return (positionals, flags);
    }

    private static async Task<int> RunScanAsync(string[] args, AppConfig cfg, ToolsInfo tools)
    {
        var (pos, flags) = ParseArgs(args);
        if (pos.Count < 1)
        {
            Console.Error.WriteLine("usage: fastblur scan <video> --out detections.json");
            return 1;
        }
        var video = pos[0];
        var outPath = flags.TryGetValue("--out", out var o) && o.Length > 0 ? o : "detections.json";
        if (!tools.FFmpegOk)
        {
            Console.Error.WriteLine("fastblur: ffmpeg not found — searched: " + tools.SearchedSummary("ffmpeg"));
            return 1;
        }
        if (!tools.TesseractOk)
        {
            Console.Error.WriteLine("fastblur: tesseract not found — searched: " + tools.SearchedSummary("tesseract"));
            return 1;
        }
        try
        {
            var info = await VideoProbe.ProbeAsync(tools, video);
            var scanner = new VideoScanner(tools);
            var dets = await scanner.ScanAsync(video, cfg.ScanInterval, cfg.Keywords, cfg.Patterns, info,
                new Progress<(int Done, int Total)>(p => Console.Write($"\rScan frame {p.Done}/{p.Total}")));
            Console.WriteLine();
            var json = JsonSerializer.Serialize(dets, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await File.WriteAllTextAsync(outPath, json);
            Console.WriteLine($"Found {dets.Count} region(s) in {video} ({info.Duration:F1}s, {info.Width}x{info.Height}) -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("fastblur: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunBlurAsync(string[] args, AppConfig cfg, ToolsInfo tools)
    {
        var (pos, flags) = ParseArgs(args);
        var regionsFile = flags.TryGetValue("--regions", out var r) ? r : "";
        var outPath = flags.TryGetValue("--out", out var o) ? o : "";
        if (pos.Count < 1 || regionsFile.Length == 0 || outPath.Length == 0)
        {
            Console.Error.WriteLine("usage: fastblur blur <video> --regions regions.json --out out.mp4");
            return 1;
        }
        if (!tools.FFmpegOk)
        {
            Console.Error.WriteLine("fastblur: ffmpeg not found — searched: " + tools.SearchedSummary("ffmpeg"));
            return 1;
        }
        try
        {
            var video = pos[0];
            var json = await File.ReadAllTextAsync(regionsFile);
            List<Region>? regions;
            try
            {
                regions = JsonSerializer.Deserialize<List<Region>>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
            catch
            {
                regions = null;
            }
            if (regions == null)
            {
                var wrap = JsonSerializer.Deserialize<RegionsWrapper>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                regions = wrap?.Regions ?? throw new InvalidOperationException("could not parse regions file");
            }
            var info = await VideoProbe.ProbeAsync(tools, video);
            foreach (var region in regions)
                if (region.W == 0 && region.Rw > 0)
                    RegionGeometry.RelativeToPixel(region, info.Width, info.Height);
            var exporter = new Exporter(tools);
            var enc = await exporter.ExportAsync(video, outPath, regions, cfg.BlurStrength,
                new Progress<double>(p => Console.Write($"\rExport {p * 100:F0}%")));
            Console.WriteLine();
            Console.WriteLine($"Wrote {outPath} ({enc})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("fastblur: " + ex.Message);
            return 1;
        }
    }

    private sealed class RegionsWrapper
    {
        public List<Region> Regions { get; set; } = new();
    }
}
