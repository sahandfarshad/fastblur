using System.Text.Json;
using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Models;

namespace FastBlur.Core.Jobs;

/// <summary>
/// Jobs: one imported video + its detections/regions/export state.
/// Persisted as jobs.json in the data dir (no DB needed).
/// Port of BlurCut's jobs.go.
/// </summary>
public sealed class JobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _dir;
    private readonly Dictionary<string, Job> _jobs = new();
    private readonly object _lock = new();

    private JobStore(string dir) => _dir = dir;

    /// <summary>
    /// Opens the store, restoring jobs from jobs.json. A scan/export running
    /// when the app was closed never finishes — it is reset instead of left
    /// stuck. A partial or broken export left over from an older version
    /// must not come back as a download link: it is checked to be a real
    /// playable video, and deleted if it is not.
    /// </summary>
    public static async Task<JobStore> OpenAsync(string dataDir, ToolsInfo tools, CancellationToken ct = default)
    {
        var store = new JobStore(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "uploads"));
        Directory.CreateDirectory(Path.Combine(dataDir, "exports"));

        var jobsFile = Path.Combine(dataDir, "jobs.json");
        if (!File.Exists(jobsFile)) return store;

        List<Job>? list;
        try
        {
            await using var fs = File.OpenRead(jobsFile);
            list = await JsonSerializer.DeserializeAsync<List<Job>>(fs, JsonOptions, ct);
        }
        catch
        {
            return store; // Corrupt jobs file: start empty rather than crash.
        }
        if (list == null) return store;

        var exporter = new Exporter(tools);
        foreach (var j in list)
        {
            if (j.Status is "scanning" or "exporting")
            {
                j.Status = "uploaded";
                j.Message = "Interrupted — please scan or export again.";
                j.Progress = 0;
            }
            // Uploads keep their original extension (id.mkv, id.mov…),
            // so find the real file instead of assuming .mp4.
            j.VideoPath = "";
            foreach (var m in Directory.GetFiles(Path.Combine(dataDir, "uploads"), j.Id + ".*"))
            {
                if (!m.EndsWith("_preview.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    j.VideoPath = m;
                    break;
                }
            }
            j.ExportPath = Path.Combine(dataDir, "exports", j.Id + "_blurred.mp4");
            j.PreviewPath = Path.Combine(dataDir, "uploads", j.Id + "_preview.mp4");
            var exportInfo = new FileInfo(j.ExportPath);
            if (exportInfo.Exists && exportInfo.Length > 0)
            {
                bool valid = true;
                if (tools.FFmpegOk || tools.FFprobe.Length > 0)
                {
                    try { await exporter.VerifyExportAsync(j.ExportPath, ct); }
                    catch { valid = false; }
                }
                if (valid) j.HasExport = true;
                else
                {
                    try { File.Delete(j.ExportPath); } catch { /* best effort */ }
                    j.HasExport = false;
                }
            }
            if (File.Exists(j.PreviewPath)) j.PreviewConverted = true;
            store._jobs[j.Id] = j;
        }
        return store;
    }

    public void Add(Job job)
    {
        lock (_lock) _jobs[job.Id] = job;
        Save();
    }

    public Job? Get(string id)
    {
        lock (_lock) return _jobs.TryGetValue(id, out var j) ? j : null;
    }

    /// <summary>Newest first (IDs are time-prefixed).</summary>
    public List<Job> List()
    {
        lock (_lock)
            return _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public void Delete(string id)
    {
        Job? job;
        lock (_lock) _jobs.Remove(id, out job);
        if (job != null)
        {
            foreach (var p in new[] { job.VideoPath, job.PreviewPath, job.ExportPath })
                try { if (p.Length > 0) File.Delete(p); } catch { /* best effort */ }
        }
        Save();
    }

    public void SetStatus(Job job, string status, string message, double progress)
    {
        lock (_lock)
        {
            job.Status = status;
            job.Message = message;
            job.Progress = progress;
        }
        Save();
    }

    public void Save()
    {
        List<Job> list;
        lock (_lock) list = _jobs.Values.ToList();
        var json = JsonSerializer.Serialize(list, JsonOptions);
        // Write via a temp file + move so a crash mid-write cannot corrupt jobs.json.
        var tmp = Path.Combine(_dir, "jobs.json.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, Path.Combine(_dir, "jobs.json"), overwrite: true);
    }

    public static string NewJobId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        + Guid.NewGuid().ToString("N")[..6];

    /// <summary>Output filename for an export — always .mp4, never the source extension.</summary>
    public static string BlurredFileName(string originalName) =>
        Path.GetFileNameWithoutExtension(originalName) + "-blurred.mp4";
}
