using System.IO.Compression;
using System.Security.Cryptography;

namespace FastBlur.Core.Ffmpeg;

/// <summary>
/// One-click fetch of ffmpeg / Tesseract when they are missing. ffmpeg comes
/// as the pinned gyan.dev essentials zip (SHA-256 verified, ffmpeg.exe +
/// ffprobe.exe extracted — no 7z needed). Tesseract ships as the official
/// UB-Mannheim installer, which is downloaded and launched for the user to
/// complete (it needs a GUI install and may need admin for C:\Program Files).
/// Port of BlurCut's tools.go.
/// </summary>
public sealed class ToolDownloader
{
    public const string FfmpegDownloadUrl =
        "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip";
    public const string FfmpegDownloadSha256 =
        "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";
    public const string TesseractDownloadUrl =
        "https://github.com/tesseract-ocr/tesseract/releases/download/5.5.3/tesseract-ocr-w64-setup-5.5.3.20260724.exe";

    private readonly string _dataDir;

    public ToolDownloader(string dataDir) => _dataDir = dataDir;

    /// <summary>
    /// Where downloaded tools go: &lt;exe&gt;/tools if writable, else
    /// &lt;dataDir&gt;/tools. Both are searched by <see cref="ToolDetector"/>.
    /// </summary>
    public string ToolsDir()
    {
        var exeTools = Path.Combine(ToolDetector.ExeDir(), "tools");
        if (ToolDetector.ExeDir().Length > 0)
        {
            try
            {
                Directory.CreateDirectory(exeTools);
                var probe = Path.Combine(exeTools, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return exeTools;
            }
            catch { /* fall through to data dir */ }
        }
        var dir = Path.Combine(_dataDir, "tools");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public enum DownloadState { Idle, Downloading, Extracting, Installing, Done, Error }

    public sealed record DownloadStatus(string Tool, DownloadState State, string Message, double Progress);

    /// <summary>
    /// Fetches the pinned gyan.dev zip, verifies its SHA-256 and extracts
    /// ffmpeg.exe (and ffprobe.exe if present) into the tools dir.
    /// </summary>
    public async Task<DownloadStatus> DownloadFfmpegAsync(
        IProgress<DownloadStatus>? progress = null, CancellationToken ct = default)
    {
        void Report(DownloadState s, string msg, double p)
            => progress?.Report(new DownloadStatus("ffmpeg", s, msg, p));

        var dir = ToolsDir();
        var zipPath = Path.Combine(dir, "ffmpeg-download.zip");
        try
        {
            Report(DownloadState.Downloading, "Downloading ffmpeg (about 110 MB)…", 0);
            await DownloadFileAsync(FfmpegDownloadUrl, zipPath,
                (got, total) =>
                {
                    if (total > 0)
                        Report(DownloadState.Downloading,
                            $"Downloading ffmpeg… {got >> 20} MB / {total >> 20} MB",
                            (double)got / total);
                }, ct);

            var sum = await Sha256FileAsync(zipPath, ct);
            if (!string.Equals(sum, FfmpegDownloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zipPath);
                return new DownloadStatus("ffmpeg", DownloadState.Error,
                    $"Download failed: checksum mismatch (got {sum})", 0);
            }

            Report(DownloadState.Extracting, "Extracting ffmpeg.exe…", 0.9);
            int extracted = 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var baseName = Path.GetFileName(entry.FullName);
                    if (baseName != "ffmpeg.exe" && baseName != "ffprobe.exe") continue;
                    entry.ExtractToFile(Path.Combine(dir, baseName), overwrite: true);
                    extracted++;
                }
            }
            File.Delete(zipPath);
            if (extracted == 0)
                return new DownloadStatus("ffmpeg", DownloadState.Error,
                    "Downloaded archive did not contain ffmpeg.exe", 0);
            return new DownloadStatus("ffmpeg", DownloadState.Done,
                $"ffmpeg installed to {dir} — restart not needed", 1);
        }
        catch (Exception ex)
        {
            return new DownloadStatus("ffmpeg", DownloadState.Error, "Download failed: " + ex.Message, 0);
        }
    }

    /// <summary>
    /// Fetches the official UB-Mannheim installer and launches it. The
    /// installer needs its GUI (and possibly admin) to finish, so the state
    /// stays "installing" until a re-check finds tesseract.
    /// Returns the installer path when it was downloaded (the caller launches
    /// it on Windows; on other platforms the user runs it by hand).
    /// </summary>
    public async Task<(DownloadStatus Status, string InstallerPath)> DownloadTesseractAsync(
        IProgress<DownloadStatus>? progress = null, CancellationToken ct = default)
    {
        void Report(DownloadState s, string msg, double p)
            => progress?.Report(new DownloadStatus("tesseract", s, msg, p));

        var dir = ToolsDir();
        var instPath = Path.Combine(dir, "tesseract-setup.exe");
        try
        {
            Report(DownloadState.Downloading, "Downloading Tesseract installer (about 26 MB)…", 0);
            await DownloadFileAsync(TesseractDownloadUrl, instPath,
                (got, total) =>
                {
                    if (total > 0)
                        Report(DownloadState.Downloading,
                            $"Downloading Tesseract installer… {got >> 20} MB / {total >> 20} MB",
                            (double)got / total);
                }, ct);

            if (!OperatingSystem.IsWindows())
                return (new DownloadStatus("tesseract", DownloadState.Done,
                    $"Installer downloaded to {instPath} — run it to install Tesseract, then click Re-check tools.", 1),
                    instPath);

            return (new DownloadStatus("tesseract", DownloadState.Installing,
                "Tesseract installer downloaded — finish the setup, then click “Re-check tools”.", 0.9),
                instPath);
        }
        catch (Exception ex)
        {
            return (new DownloadStatus("tesseract", DownloadState.Error, "Download failed: " + ex.Message, 0), "");
        }
    }

    public static async Task DownloadFileAsync(
        string url, string dest, Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"download {url}: HTTP {(int)resp.StatusCode}");
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        var buf = new byte[256 * 1024];
        long got = 0;
        int n;
        while ((n = await stream.ReadAsync(buf, ct)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, n), ct);
            got += n;
            onProgress?.Invoke(got, total);
        }
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var f = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(f, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
