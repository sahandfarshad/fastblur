using System.Diagnostics;
using FastBlur.Core.Models;

namespace FastBlur.Core.Ffmpeg;

/// <summary>
/// Finds ffmpeg / ffprobe / tesseract next to the exe, in tools/, on PATH,
/// or in the usual Windows install folders — same order as BlurCut's ffmpeg.go.
/// Injectable exe dir / cwd / PATH lookup so tests can use temp dirs without
/// touching the real system.
/// </summary>
public static class ToolDetector
{
    public static ToolsInfo Detect(AppConfig cfg) =>
        DetectFrom(cfg, ExeDir(), Directory.GetCurrentDirectory(), name =>
        {
            try
            {
                var found = Which(name);
                return found;
            }
            catch { return null; }
        });

    public static string ExeDir()
    {
        var exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe) ? "" : Path.GetDirectoryName(exe) ?? "";
    }

    private static string? Which(string name)
    {
        // Cross-platform PATH lookup (Process.Start would do this too, but
        // detection needs the resolved path for display).
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exeNames = OperatingSystem.IsWindows()
            ? new[] { name, name + ".exe" }
            : new[] { name };
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var fn in exeNames)
            {
                var p = Path.Combine(dir, fn);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    public static ToolsInfo DetectFrom(
        AppConfig cfg, string exeDir, string cwd, Func<string, string?> lookPath)
    {
        var t = new ToolsInfo();
        t.FFmpeg = ResolveTool("ffmpeg", cfg.FfmpegPath, "FASTBLUR_FFMPEG", exeDir, cwd, lookPath, out var searched);
        t.Searched["ffmpeg"] = searched;

        // ffprobe: prefer the folder ffmpeg was found in (gyan bin/ ships both).
        var ffprobeSearched = new List<string>();
        if (t.FFmpeg.Length > 0)
        {
            var dir = Path.GetDirectoryName(t.FFmpeg) ?? "";
            foreach (var fn in new[] { "ffprobe.exe", "ffprobe" })
            {
                var p = Path.Combine(dir, fn);
                ffprobeSearched.Add(p);
                if (File.Exists(p)) { t.FFprobe = p; break; }
            }
        }
        if (t.FFprobe.Length == 0)
        {
            t.FFprobe = ResolveTool("ffprobe", cfg.FfprobePath, "FASTBLUR_FFPROBE", exeDir, cwd, lookPath, out var s2);
            ffprobeSearched.AddRange(s2);
        }
        t.Searched["ffprobe"] = ffprobeSearched;

        t.Tesseract = ResolveTool("tesseract", cfg.TesseractPath, "FASTBLUR_TESSERACT", exeDir, cwd, lookPath, out var searched3);
        t.Searched["tesseract"] = searched3;

        // TESSDATA_PREFIX: env wins, else discover next to the binary / exe.
        // It must point AT the tessdata dir itself.
        var tessEnv = Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? "";
        if (tessEnv.Length > 0)
        {
            if (File.Exists(Path.Combine(tessEnv, "eng.traineddata"))) t.TessData = tessEnv;
            else if (File.Exists(Path.Combine(tessEnv, "tessdata", "eng.traineddata"))) t.TessData = Path.Combine(tessEnv, "tessdata");
        }
        else
        {
            t.TessData = FindTessData(t.Tesseract, exeDir);
        }

        if (t.FFmpeg.Length > 0)
        {
            try
            {
                var psi = new ProcessStartInfo(t.FFmpeg)
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-encoders");
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    t.HasNvenc = output.Contains("h264_nvenc", StringComparison.Ordinal);
                }
            }
            catch { /* NVENC check is best-effort */ }
        }
        return t;
    }

    /// <summary>
    /// Folders searched for a tool, in priority order (after a config path
    /// and env var, which are handled by the caller).
    /// </summary>
    public static List<string> CandidateDirs(string exeDir, string cwd)
    {
        var dirs = new List<string>();
        void Add(string d)
        {
            if (d.Length > 0 && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
        }
        Add(exeDir);
        Add(Path.Combine(exeDir, "tools"));
        Add(Path.Combine(exeDir, "bin"));
        Add(cwd);
        Add(Path.Combine(cwd, "tools"));
        if (OperatingSystem.IsWindows())
        {
            Add(@"C:\ffmpeg\bin");
            Add(@"C:\Program Files\ffmpeg\bin");
            Add(@"C:\Program Files\Tesseract-OCR");
            Add(@"C:\Program Files (x86)\Tesseract-OCR");
            var la = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(la)) Add(Path.Combine(la, "Tesseract-OCR"));
        }
        return dirs;
    }

    /// <summary>
    /// Resolves one tool, recording every place it looked.
    /// Order: config path, env var, exe folder (+ tools/, bin/), cwd (+ tools/),
    /// PATH, then Windows common install folders.
    /// </summary>
    public static string ResolveTool(
        string name, string cfgPath, string envVar, string exeDir, string cwd,
        Func<string, string?> lookPath, out List<string> searched)
    {
        searched = new List<string>();
        var fileNames = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { name }
            : new[] { name, name + ".exe" };

        // (a) config-saved path
        if (cfgPath.Length > 0)
        {
            searched.Add(cfgPath);
            if (File.Exists(cfgPath)) return cfgPath;
        }
        // (b) env var
        if (envVar.Length > 0)
        {
            var v = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(v))
            {
                searched.Add(v);
                if (File.Exists(v)) return v;
            }
        }
        // (c)-(d) exe folder, tools/, bin/, cwd, tools/
        foreach (var dir in CandidateDirs(exeDir, cwd))
        foreach (var fn in fileNames)
        {
            var p = Path.Combine(dir, fn);
            searched.Add(p);
            if (File.Exists(p)) return p;
        }
        // (e) PATH
        var found = lookPath(name);
        if (found != null)
        {
            searched.Add("PATH: " + found);
            return found;
        }
        searched.Add("PATH: " + name + " (not found)");
        return "";
    }

    /// <summary>
    /// Locates a tessdata folder containing eng.traineddata next to the
    /// tesseract binary or the exe. Returns the tessdata dir itself —
    /// tesseract wants TESSDATA_PREFIX pointing AT tessdata, not its parent.
    /// </summary>
    public static string FindTessData(string tesseractPath, string exeDir)
    {
        var dirs = new List<string>();
        if (tesseractPath.Length > 0)
        {
            var d = Path.GetDirectoryName(tesseractPath) ?? "";
            dirs.Add(d);
            dirs.Add(Path.Combine(d, "tessdata"));
        }
        dirs.Add(exeDir);
        dirs.Add(Path.Combine(exeDir, "tessdata"));
        dirs.Add(Path.Combine(exeDir, "tools"));
        dirs.Add(Path.Combine(exeDir, "tools", "tessdata"));

        foreach (var d in dirs)
        {
            if (d.Length == 0) continue;
            // d may itself be the tessdata dir, or its parent.
            foreach (var cand in new[] { d, Path.Combine(d, "tessdata") })
                if (File.Exists(Path.Combine(cand, "eng.traineddata")))
                    return cand;
        }
        return "";
    }
}
