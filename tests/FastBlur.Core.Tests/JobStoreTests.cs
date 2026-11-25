using System.Text.Json;
using FastBlur.Core.Config;
using FastBlur.Core.Jobs;
using FastBlur.Core.Models;

namespace FastBlur.Core.Tests;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "fastblur-jobtest-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task OpenAsync_EmptyDir_StartsEmpty()
    {
        var store = await JobStore.OpenAsync(_tmp, new ToolsInfo());
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Add_And_List_RoundTrip()
    {
        var store = await JobStore.OpenAsync(_tmp, new ToolsInfo());
        var job = new Job { Id = "1", Name = "clip", Duration = 10, Status = "uploaded" };
        store.Add(job);
        Assert.Single(store.List());
        Assert.Equal("clip", store.Get("1")!.Name);

        // Re-open: persisted to jobs.json.
        var reopened = await JobStore.OpenAsync(_tmp, new ToolsInfo());
        Assert.Single(reopened.List());
        Assert.Equal("clip", reopened.Get("1")!.Name);
    }

    [Fact]
    public async Task OpenAsync_InterruptedScan_IsResetToUploaded()
    {
        var uploads = Directory.CreateDirectory(Path.Combine(_tmp, "uploads")).FullName;
        File.WriteAllText(Path.Combine(uploads, "42.mp4"), "fake video");
        var job = new Job { Id = "42", Name = "clip", Duration = 10, Status = "scanning", Progress = 0.5 };
        await File.WriteAllTextAsync(Path.Combine(_tmp, "jobs.json"),
            JsonSerializer.Serialize(new[] { job }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var store = await JobStore.OpenAsync(_tmp, new ToolsInfo());
        var restored = store.Get("42")!;
        Assert.Equal("uploaded", restored.Status);
        Assert.Equal(0, restored.Progress);
        Assert.Contains("Interrupted", restored.Message);
        Assert.Equal(Path.Combine(uploads, "42.mp4"), restored.VideoPath);
    }

    [Fact]
    public async Task OpenAsync_CorruptExport_IsDeleted_NotOffered()
    {
        var uploads = Directory.CreateDirectory(Path.Combine(_tmp, "uploads")).FullName;
        var exports = Directory.CreateDirectory(Path.Combine(_tmp, "exports")).FullName;
        File.WriteAllText(Path.Combine(uploads, "7.mp4"), "fake video");
        // Non-empty but not a real video: verify must fail and delete it.
        var exportPath = Path.Combine(exports, "7_blurred.mp4");
        await File.WriteAllTextAsync(exportPath, "this is not a video");
        var job = new Job { Id = "7", Name = "clip", Duration = 10, Status = "done", HasExport = true };
        await File.WriteAllTextAsync(Path.Combine(_tmp, "jobs.json"),
            JsonSerializer.Serialize(new[] { job }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // Point ffmpeg at a path that does not exist: probing fails, so the
        // export cannot be verified and must be removed.
        var tools = new ToolsInfo { FFmpeg = Path.Combine(_tmp, "no-such-ffmpeg") };
        var store = await JobStore.OpenAsync(_tmp, tools);
        var restored = store.Get("7")!;
        Assert.False(restored.HasExport);
        Assert.False(File.Exists(exportPath));
    }

    [Fact]
    public async Task OpenAsync_ValidExport_IsKept()
    {
        var uploads = Directory.CreateDirectory(Path.Combine(_tmp, "uploads")).FullName;
        var exports = Directory.CreateDirectory(Path.Combine(_tmp, "exports")).FullName;
        File.WriteAllText(Path.Combine(uploads, "8.mp4"), "fake video");
        var exportPath = Path.Combine(exports, "8_blurred.mp4");
        await File.WriteAllTextAsync(exportPath, "pretend video bytes");
        var job = new Job { Id = "8", Name = "clip", Duration = 10, Status = "done", HasExport = true };
        await File.WriteAllTextAsync(Path.Combine(_tmp, "jobs.json"),
            JsonSerializer.Serialize(new[] { job }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // No ffmpeg available: restore falls back to the size check.
        var store = await JobStore.OpenAsync(_tmp, new ToolsInfo());
        Assert.True(store.Get("8")!.HasExport);
        Assert.True(File.Exists(exportPath));
    }

    [Fact]
    public async Task Delete_RemovesJobAndFiles()
    {
        var store = await JobStore.OpenAsync(_tmp, new ToolsInfo());
        var video = Path.Combine(_tmp, "uploads", "9.mp4");
        var export = Path.Combine(_tmp, "exports", "9_blurred.mp4");
        File.WriteAllText(video, "v");
        File.WriteAllText(export, "e");
        var job = new Job { Id = "9", Name = "clip", VideoPath = video, ExportPath = export };
        store.Add(job);
        store.Delete("9");
        Assert.Null(store.Get("9"));
        Assert.False(File.Exists(video));
        Assert.False(File.Exists(export));
    }

    [Fact]
    public void BlurredFileName_AlwaysMp4()
    {
        Assert.Equal("clip-blurred.mp4", JobStore.BlurredFileName("clip.mkv"));
        Assert.Equal("clip-blurred.mp4", JobStore.BlurredFileName("clip.mov"));
        Assert.Equal("clip-blurred.mp4", JobStore.BlurredFileName("clip.mp4"));
    }

    [Fact]
    public void NewJobId_IsUnique()
    {
        Assert.NotEqual(JobStore.NewJobId(), JobStore.NewJobId());
    }
}

public sealed class AppConfigTests : IDisposable
{
    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "fastblur-cfgtest-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = AppConfigStore.Load(_tmp);
        Assert.Equal(1.0, cfg.ScanInterval);
        Assert.Equal(20, cfg.BlurStrength);
        Assert.True(cfg.Patterns["url"]);
        Assert.True(cfg.Patterns["crypto"]);
        Assert.False(cfg.Patterns["suspicious"]); // possible-key is opt-in, default OFF
    }

    [Fact]
    public void Normalize_ClampsOutOfRange()
    {
        var cfg = new AppConfig { ScanInterval = 99, BlurStrength = 1 };
        cfg.Normalize();
        Assert.Equal(1.0, cfg.ScanInterval);
        Assert.Equal(20, cfg.BlurStrength);
    }

    [Fact]
    public void Save_And_Load_RoundTrip()
    {
        var cfg = new AppConfig { ScanInterval = 0.5, BlurStrength = 30, Keywords = new List<string> { "demo" } };
        cfg.Patterns["suspicious"] = true;
        AppConfigStore.Save(_tmp, cfg);
        var loaded = AppConfigStore.Load(_tmp);
        Assert.Equal(0.5, loaded.ScanInterval);
        Assert.Equal(30, loaded.BlurStrength);
        Assert.Contains("demo", loaded.Keywords);
        Assert.True(loaded.Patterns["suspicious"]);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(Path.Combine(_tmp, "config.json"), "{ not json");
        var cfg = AppConfigStore.Load(_tmp);
        Assert.Equal(1.0, cfg.ScanInterval);
    }
}
