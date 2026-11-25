using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using FastBlur.Core.Config;
using FastBlur.Core.Detect;
using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Jobs;
using FastBlur.Core.Models;
using FastBlur.Core.Ocr;
using Microsoft.Win32;

namespace FastBlur.ViewModels;

/// <summary>
/// Main window state. The view (MainWindow) owns the MediaElement and the
/// box overlay canvas; this ViewModel owns jobs, regions, tools, settings
/// and the scan/export work. The view subscribes to <see cref="SeekRequested"/>
/// and <see cref="PlaybackSourceChanged"/>.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly string _dataDir;
    private readonly JobStore _store;
    private Job? _currentJob;
    private string _statusMessage = "";
    private double _progress;
    private bool _isBusy;
    private bool _drawMode;
    private double _currentTime;
    private string _playbackPath = "";
    private string _exportOutputPath = "";
    private Region? _selectedRegion;
    private string _selectedRegionStartText = "";
    private string _selectedRegionEndText = "";

    public MainViewModel(string dataDir, AppConfig config, ToolsInfo tools, JobStore store)
    {
        _dataDir = dataDir;
        Config = config;
        Tools = tools;
        _store = store;
        Jobs = new ObservableCollection<Job>(store.List());
        Regions = new ObservableCollection<Region>();
        OcrWords = new ObservableCollection<OcrWord>();

        OpenVideoCommand = new RelayCommand(_ => OpenVideoDialog());
        ScanCommand = new AsyncRelayCommand(() => ScanAsync(), () => CurrentJob != null && Tools.TesseractOk && !IsBusy);
        OcrPreviewCommand = new AsyncRelayCommand(() => OcrPreviewAsync(), () => CurrentJob != null && Tools.TesseractOk && !IsBusy);
        ExportCommand = new AsyncRelayCommand(() => ExportAsync(), () => CurrentJob != null && Regions.Count > 0 && !IsBusy);
        DeleteJobCommand = new RelayCommand(p => { if (p is Job j) DeleteJob(j); });
        DeleteRegionCommand = new RelayCommand(p => { if (p is Region r) DeleteRegion(r); });
        ToggleDrawModeCommand = new RelayCommand(_ => DrawMode = !DrawMode);
        RecheckToolsCommand = new RelayCommand(_ => RecheckTools());
        DownloadFfmpegCommand = new AsyncRelayCommand(() => DownloadFfmpegAsync(), () => !Tools.FFmpegOk && !IsBusy);
        DownloadTesseractCommand = new AsyncRelayCommand(() => DownloadTesseractAsync(), () => !Tools.TesseractOk && !IsBusy);
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        SaveToolPathsCommand = new RelayCommand(_ => SaveToolPaths());
        ApplyRegionTimesCommand = new RelayCommand(_ => ApplyRegionTimes(), _ => SelectedRegion != null);
        SeekToRegionCommand = new RelayCommand(p => { if (p is Region r) SeekRequested?.Invoke(this, r.Start); });
        OpenExportFolderCommand = new RelayCommand(_ => OpenExportFolder(), _ => ExportOutputPath.Length > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised when the view should seek the player to a time (seconds).</summary>
    public event EventHandler<double>? SeekRequested;
    /// <summary>Raised when the file the player should play changed.</summary>
    public event EventHandler? PlaybackSourceChanged;
    /// <summary>Raised when the region list changed and the overlay must re-render.</summary>
    public event EventHandler? RegionsChanged;

    public AppConfig Config { get; }
    public ToolsInfo Tools { get; private set; }

    public string FfmpegStatus => Tools.FFmpegOk
        ? $"ffmpeg: found — {Tools.FFmpeg}"
        : "ffmpeg: not found — download it below, or put ffmpeg.exe next to FastBlur.exe.";
    public string TesseractStatus => Tools.TesseractOk
        ? $"tesseract: found — {Tools.Tesseract}"
        : "tesseract: not found — auto-scan is off until Tesseract is found; manual boxes still work.";
    public ObservableCollection<Job> Jobs { get; }
    public ObservableCollection<Region> Regions { get; }
    public ObservableCollection<OcrWord> OcrWords { get; }

    public Job? CurrentJob
    {
        get => _currentJob;
        private set { _currentJob = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public bool DrawMode
    {
        get => _drawMode;
        set { _drawMode = value; OnPropertyChanged(); StatusMessage = value ? "Drawing mode: drag a box on the video. Click again to stop." : ""; }
    }

    /// <summary>Current playback position in seconds (updated by the view's timer).</summary>
    public double CurrentTime
    {
        get => _currentTime;
        set { _currentTime = value; OnPropertyChanged(); }
    }

    public double Duration => CurrentJob?.Duration ?? 0;

    public string PlaybackPath
    {
        get => _playbackPath;
        private set { _playbackPath = value; OnPropertyChanged(); PlaybackSourceChanged?.Invoke(this, EventArgs.Empty); }
    }

    public string ExportOutputPath
    {
        get => _exportOutputPath;
        private set { _exportOutputPath = value; OnPropertyChanged(); }
    }

    public Region? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            _selectedRegion = value;
            OnPropertyChanged();
            SelectedRegionStartText = value == null ? "" : value.Start.ToString("F2");
            SelectedRegionEndText = value == null ? "" : value.End.ToString("F2");
        }
    }

    public string SelectedRegionStartText
    {
        get => _selectedRegionStartText;
        set { _selectedRegionStartText = value; OnPropertyChanged(); }
    }

    public string SelectedRegionEndText
    {
        get => _selectedRegionEndText;
        set { _selectedRegionEndText = value; OnPropertyChanged(); }
    }

    public string KeywordsText
    {
        get => string.Join(", ", Config.Keywords);
        set => Config.Keywords = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public bool PatternUrl
    {
        get => Config.Patterns.GetValueOrDefault("url");
        set { Config.Patterns["url"] = value; OnPropertyChanged(); }
    }
    public bool PatternEmail
    {
        get => Config.Patterns.GetValueOrDefault("email");
        set { Config.Patterns["email"] = value; OnPropertyChanged(); }
    }
    public bool PatternPassword
    {
        get => Config.Patterns.GetValueOrDefault("password");
        set { Config.Patterns["password"] = value; OnPropertyChanged(); }
    }
    public bool PatternApiKey
    {
        get => Config.Patterns.GetValueOrDefault("apikey");
        set { Config.Patterns["apikey"] = value; OnPropertyChanged(); }
    }
    public bool PatternCrypto
    {
        get => Config.Patterns.GetValueOrDefault("crypto");
        set { Config.Patterns["crypto"] = value; OnPropertyChanged(); }
    }
    public bool PatternPossibleKey
    {
        get => Config.Patterns.GetValueOrDefault("suspicious");
        set { Config.Patterns["suspicious"] = value; OnPropertyChanged(); }
    }

    public ICommand OpenVideoCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand OcrPreviewCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand DeleteJobCommand { get; }
    public ICommand DeleteRegionCommand { get; }
    public ICommand ToggleDrawModeCommand { get; }
    public ICommand RecheckToolsCommand { get; }
    public ICommand DownloadFfmpegCommand { get; }
    public ICommand DownloadTesseractCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand SaveToolPathsCommand { get; }
    public ICommand ApplyRegionTimesCommand { get; }
    public ICommand SeekToRegionCommand { get; }
    public ICommand OpenExportFolderCommand { get; }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OpenVideoDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open video",
            Filter = "Videos (*.mp4;*.mov;*.mkv)|*.mp4;*.mov;*.mkv|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            _ = OpenVideoAsync(dlg.FileName);
    }

    /// <summary>Imports a video file as a new job (also the drag-drop entry point).</summary>
    public async Task OpenVideoAsync(string path)
    {
        if (!Tools.FFmpegOk)
        {
            StatusMessage = "ffmpeg not found — download it in the Tools section, or put ffmpeg.exe next to FastBlur.exe.";
            return;
        }
        try
        {
            IsBusy = true;
            StatusMessage = "Probing video…";
            var info = await VideoProbe.ProbeAsync(Tools, path);
            var id = JobStore.NewJobId();
            var ext = Path.GetExtension(path);
            var dest = Path.Combine(_dataDir, "uploads", id + ext);
            File.Copy(path, dest, overwrite: true);
            var job = new Job
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(path),
                Duration = info.Duration,
                Width = info.Width,
                Height = info.Height,
                Fps = info.Fps,
                Status = "uploaded",
                VideoPath = dest,
                ExportPath = Path.Combine(_dataDir, "exports", id + "_blurred.mp4"),
                PreviewPath = Path.Combine(_dataDir, "uploads", id + "_preview.mp4"),
            };
            _store.Add(job);
            Jobs.Insert(0, job);
            await SelectJobAsync(job);
            StatusMessage = "Uploaded. Now press Auto-scan, or draw a manual box.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open video: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    public async Task SelectJobAsync(Job job)
    {
        CurrentJob = job;
        Regions.Clear();
        foreach (var r in job.Regions) Regions.Add(r);
        OcrWords.Clear();
        ExportOutputPath = job.HasExport ? job.ExportPath : "";
        OnPropertyChanged(nameof(Duration));
        RegionsChanged?.Invoke(this, EventArgs.Empty);
        await PreparePlaybackAsync(job);
    }

    /// <summary>
    /// Picks the file the player should play. MediaElement plays mp4/h264
    /// directly; .mkv/.mov/HEVC need a converted preview mp4 (made once
    /// and reused, like the Go version).
    /// </summary>
    private async Task PreparePlaybackAsync(Job job)
    {
        if (job.VideoPath.Length == 0 || !File.Exists(job.VideoPath))
        {
            StatusMessage = "Video file is missing — it may have been moved or deleted.";
            return;
        }
        try
        {
            var info = await VideoProbe.ProbeAsync(Tools, job.VideoPath);
            if (PlaybackSupport.IsDirectlyPlayable(info))
            {
                job.PreviewConverted = false;
                PlaybackPath = job.VideoPath;
                return;
            }
            if (File.Exists(job.PreviewPath))
            {
                job.PreviewConverted = true;
                PlaybackPath = job.PreviewPath;
                StatusMessage = "Preview converted — playing the converted mp4 (scan/export still use the original).";
                return;
            }
            StatusMessage = "Converting preview…";
            var exporter = new Exporter(Tools);
            await exporter.MakePreviewAsync(job.VideoPath, job.PreviewPath, info);
            job.PreviewConverted = true;
            _store.Save();
            PlaybackPath = job.PreviewPath;
            StatusMessage = "Preview converted — playing the converted mp4 (scan/export still use the original).";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not prepare preview: " + ex.Message;
        }
    }

    private async Task ScanAsync()
    {
        var job = CurrentJob;
        if (job == null || !Tools.TesseractOk) return;
        try
        {
            IsBusy = true;
            _store.SetStatus(job, "scanning", "Scanning…", 0);
            var info = await VideoProbe.ProbeAsync(Tools, job.VideoPath);
            var scanner = new VideoScanner(Tools);
            var dets = await scanner.ScanAsync(job.VideoPath, Config.ScanInterval, Config.Keywords, Config.Patterns, info,
                new Progress<(int Done, int Total)>(p =>
                {
                    Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                    StatusMessage = $"Scanning frame {p.Done}/{p.Total}…";
                }));
            job.Detections = dets;
            var fresh = dets.Select(d => (Region)d).ToList();
            var merged = RegionGeometry.MergeScanRegions(job.Regions, fresh);
            job.Regions = merged;
            Regions.Clear();
            foreach (var r in merged) Regions.Add(r);
            RegionsChanged?.Invoke(this, EventArgs.Empty);
            _store.SetStatus(job, "scanned", $"Scan done — {merged.Count} region(s).", 1);
            StatusMessage = merged.Count == 0
                ? "Scan found nothing sensitive. Try “What does the scan see here?” to check what OCR reads, or draw a manual box."
                : $"Scan done — {merged.Count} region(s). Review them, then Export.";
            Progress = 0;
        }
        catch (Exception ex)
        {
            _store.SetStatus(job, "error", ex.Message, 0);
            StatusMessage = "Scan failed: " + ex.Message;
            Progress = 0;
        }
        finally { IsBusy = false; }
    }

    private async Task OcrPreviewAsync()
    {
        var job = CurrentJob;
        if (job == null || !Tools.TesseractOk) return;
        try
        {
            IsBusy = true;
            StatusMessage = "Reading this frame…";
            var info = await VideoProbe.ProbeAsync(Tools, job.VideoPath);
            var scanner = new VideoScanner(Tools);
            var words = await scanner.OcrPreviewFrameAsync(job.VideoPath, CurrentTime, info);
            OcrWords.Clear();
            foreach (var w in words) OcrWords.Add(w);
            StatusMessage = words.Count == 0
                ? "Tesseract sees no words at this frame — try another moment, or check the scan interval."
                : $"Tesseract sees {words.Count} word(s) at {CurrentTime:F1}s.";
        }
        catch (Exception ex)
        {
            StatusMessage = "OCR preview failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task ExportAsync()
    {
        var job = CurrentJob;
        if (job == null) return;
        var enabled = Regions.Where(r => r.Enabled).ToList();
        if (enabled.Count == 0)
        {
            StatusMessage = "No enabled blur regions — enable at least one, or draw a box.";
            return;
        }
        try
        {
            IsBusy = true;
            _store.SetStatus(job, "exporting", "Exporting…", 0);
            var exporter = new Exporter(Tools);
            var enc = await exporter.ExportAsync(job.VideoPath, job.ExportPath, enabled, Config.BlurStrength,
                new Progress<double>(p =>
                {
                    Progress = p;
                    StatusMessage = $"Exporting {p * 100:F0}%…";
                }));
            await exporter.VerifyExportAsync(job.ExportPath);
            job.HasExport = true;
            _store.SetStatus(job, "done", $"Export done ({enc}).", 1);
            ExportOutputPath = job.ExportPath;
            var sizeMb = new FileInfo(job.ExportPath).Length / 1048576.0;
            StatusMessage = $"Export done — {Path.GetFileName(job.ExportPath)} ({sizeMb:F1} MB, {enc}).";
            Progress = 0;
        }
        catch (Exception ex)
        {
            _store.SetStatus(job, "error", ex.Message, 0);
            StatusMessage = "Export failed: " + ex.Message;
            Progress = 0;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Adds a manual box drawn on the preview. Time range defaults
    /// to the current position ±2.5s, clamped to the video.</summary>
    public void AddManualRegion(double rx, double ry, double rw, double rh)
    {
        var job = CurrentJob;
        if (job == null) return;
        double start = Math.Max(0, CurrentTime - 2.5);
        double end = Math.Min(job.Duration, CurrentTime + 2.5);
        if (end <= start) end = Math.Min(job.Duration, start + 5);
        var r = new Region
        {
            Id = "manual-" + Guid.NewGuid().ToString("N")[..8],
            Label = "manual",
            Text = "",
            Rx = rx, Ry = ry, Rw = rw, Rh = rh,
            Start = start, End = end, Enabled = true, Source = "manual",
        };
        RegionGeometry.RelativeToPixel(r, job.Width, job.Height);
        job.Regions.Add(r);
        Regions.Add(r);
        RegionsChanged?.Invoke(this, EventArgs.Empty);
        _store.Save();
        StatusMessage = $"Manual box added ({start:F1}s – {end:F1}s). Edit its times in the region list if needed.";
    }

    public void DeleteRegion(Region region)
    {
        var job = CurrentJob;
        if (job == null) return;
        job.Regions.Remove(region);
        Regions.Remove(region);
        if (SelectedRegion == region) SelectedRegion = null;
        RegionsChanged?.Invoke(this, EventArgs.Empty);
        _store.Save();
    }

    public void DeleteJob(Job job)
    {
        _store.Delete(job.Id);
        Jobs.Remove(job);
        if (CurrentJob == job)
        {
            CurrentJob = null;
            Regions.Clear();
            OcrWords.Clear();
            PlaybackPath = "";
            ExportOutputPath = "";
            RegionsChanged?.Invoke(this, EventArgs.Empty);
        }
        StatusMessage = $"Deleted job “{job.Name}”.";
    }

    /// <summary>Applies the edited start/end times of the selected region.</summary>
    public void ApplyRegionTimes()
    {
        var r = SelectedRegion;
        var job = CurrentJob;
        if (r == null || job == null) return;
        if (!double.TryParse(SelectedRegionStartText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var start)
            || !double.TryParse(SelectedRegionEndText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var end))
        {
            StatusMessage = "Times must be numbers in seconds, e.g. 12.5.";
            return;
        }
        var err = RegionGeometry.ValidateTimeRange(start, end, job.Duration);
        if (err.Length > 0)
        {
            StatusMessage = err;
            return;
        }
        r.Start = start;
        r.End = end;
        _store.Save();
        StatusMessage = $"Region times updated ({start:F1}s – {end:F1}s).";
    }

    public void NotifyRegionsChanged() => RegionsChanged?.Invoke(this, EventArgs.Empty);

    public void SaveSettings()
    {
        Config.Normalize();
        AppConfigStore.Save(_dataDir, Config);
        OnPropertyChanged(nameof(KeywordsText));
        StatusMessage = "Settings saved.";
    }

    public void SaveToolPaths()
    {
        AppConfigStore.Save(_dataDir, Config);
        RecheckTools();
        StatusMessage = "Tool paths saved — re-checked.";
    }

    public void RecheckTools()
    {
        Tools = ToolDetector.Detect(Config);
        OnPropertyChanged(nameof(Tools));
        OnPropertyChanged(nameof(FfmpegStatus));
        OnPropertyChanged(nameof(TesseractStatus));
        StatusMessage = Tools.FFmpegOk && Tools.TesseractOk
            ? "Tools OK: ffmpeg and tesseract found."
            : "Tools re-checked — see the Tools section for what's missing.";
    }

    private async Task DownloadFfmpegAsync()
    {
        var downloader = new ToolDownloader(_dataDir);
        IsBusy = true;
        try
        {
            var status = await downloader.DownloadFfmpegAsync(new Progress<ToolDownloader.DownloadStatus>(s =>
            {
                StatusMessage = s.Message;
                Progress = s.Progress;
            }));
            StatusMessage = status.Message;
            if (status.State == ToolDownloader.DownloadState.Done) RecheckTools();
        }
        finally { IsBusy = false; Progress = 0; }
    }

    private async Task DownloadTesseractAsync()
    {
        var downloader = new ToolDownloader(_dataDir);
        IsBusy = true;
        try
        {
            var (status, installer) = await downloader.DownloadTesseractAsync(new Progress<ToolDownloader.DownloadStatus>(s =>
            {
                StatusMessage = s.Message;
                Progress = s.Progress;
            }));
            StatusMessage = status.Message;
            if (status.State == ToolDownloader.DownloadState.Installing && installer.Length > 0)
            {
                // Launch the installer GUI; the user clicks through it.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = true });
            }
            else if (status.State == ToolDownloader.DownloadState.Done) RecheckTools();
        }
        finally { IsBusy = false; Progress = 0; }
    }

    private void OpenExportFolder()
    {
        if (ExportOutputPath.Length == 0) return;
        var dir = Path.GetDirectoryName(ExportOutputPath);
        if (dir != null && Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{ExportOutputPath}\"") { UseShellExecute = true });
    }
}
