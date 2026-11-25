using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FastBlur.Core.Models;
using FastBlur.ViewModels;

namespace FastBlur;

/// <summary>
/// View code: owns the MediaElement, the box overlay canvas, drawing,
/// fullscreen and the playback timer. All data lives in
/// <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private bool _seeking;
    private bool _isFullscreen;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private Point? _drawStart;
    private Border? _drawBox;
    private string _activeBoxKey = "";

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.SeekRequested += (_, t) => SeekTo(t);
        _vm.PlaybackSourceChanged += (_, _) => LoadPlaybackSource();
        _vm.RegionsChanged += (_, _) => RenderBoxes();
        BoxCanvas.SizeChanged += (_, _) => RenderBoxes();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DrawMode))
        {
            // The overlay only takes mouse input while drawing. At all other
            // times clicks must reach the video and its controls.
            BoxCanvas.IsHitTestVisible = _vm.DrawMode;
            DrawHint.Visibility = _vm.DrawMode ? Visibility.Visible : Visibility.Collapsed;
            DrawToggleBtn.Content = _vm.DrawMode ? "Drawing: ON (click to stop)" : "Draw a box";
        }
    }

    // ---- Playback ----

    private void LoadPlaybackSource()
    {
        if (_vm.PlaybackPath.Length == 0) return;
        Player.Source = new Uri(_vm.PlaybackPath);
        EmptyHint.Visibility = Visibility.Collapsed;
        Player.Play();
        PlayPauseBtn.Content = "Pause";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source == null) return;
        if (Player.CanPause)
        {
            Player.Pause();
            PlayPauseBtn.Content = "Play";
        }
        else
        {
            Player.Play();
            PlayPauseBtn.Content = "Pause";
        }
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        PlayPauseBtn.Content = "Play";
    }

    private void SeekTo(double seconds)
    {
        if (Player.Source == null) return;
        Player.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    private void SeekSlider_DragStart(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void SeekSlider_DragEnd(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        if (_vm.Duration > 0)
            SeekTo(SeekSlider.Value * _vm.Duration);
    }

    private void OnTimerTick()
    {
        if (Player.Source == null || Player.NaturalDuration.HasTimeSpan == false) return;
        var pos = Player.Position.TotalSeconds;
        _vm.CurrentTime = pos;
        if (!_seeking && _vm.Duration > 0)
        {
            SeekSlider.Value = pos / _vm.Duration;
            TimeText.Text = $"{FormatTime(pos)} / {FormatTime(_vm.Duration)}";
        }
        RenderBoxesIfChanged();
    }

    private static string FormatTime(double s) => $"{(int)s / 60}:{(int)s % 60:D2}";

    // ---- Box overlay ----

    /// <summary>
    /// Maps the video frame onto the canvas. MediaElement uses Uniform
    /// stretch, so the video may be letterboxed — boxes must map to the
    /// displayed video rect, not the whole canvas (the Go v0.2.7 fix for
    /// boxes blurring only ~80% of what was drawn).
    /// </summary>
    private (double Scale, double OffsetX, double OffsetY) VideoTransform()
    {
        var job = _vm.CurrentJob;
        if (job == null || job.Width <= 0 || job.Height <= 0)
            return (1, 0, 0);
        double scale = Math.Min(BoxCanvas.ActualWidth / job.Width, BoxCanvas.ActualHeight / job.Height);
        double offsetX = (BoxCanvas.ActualWidth - job.Width * scale) / 2;
        double offsetY = (BoxCanvas.ActualHeight - job.Height * scale) / 2;
        return (scale, offsetX, offsetY);
    }

    private void RenderBoxesIfChanged()
    {
        var key = string.Join(",", ActiveRegions().Select(r => r.Id));
        if (key == _activeBoxKey) return;
        _activeBoxKey = key;
        RenderBoxes();
    }

    private IEnumerable<Region> ActiveRegions()
    {
        var t = _vm.CurrentTime;
        return _vm.Regions.Where(r =>
            (r.Start <= t && t <= r.End) || r == _vm.SelectedRegion);
    }

    public void RenderBoxes()
    {
        BoxCanvas.Children.Clear();
        _activeBoxKey = string.Join(",", ActiveRegions().Select(r => r.Id));
        var job = _vm.CurrentJob;
        if (job == null) return;
        var (scale, offsetX, offsetY) = VideoTransform();
        foreach (var r in ActiveRegions())
        {
            var border = new Border
            {
                BorderBrush = Brushes.Yellow,
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 0, 0)),
                Width = Math.Max(2, r.Rw * job.Width * scale),
                Height = Math.Max(2, r.Rh * job.Height * scale),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(border, offsetX + r.Rx * job.Width * scale);
            Canvas.SetTop(border, offsetY + r.Ry * job.Height * scale);
            BoxCanvas.Children.Add(border);

            var label = new System.Windows.Controls.TextBlock
            {
                Text = $"{r.Label} {r.Start:F1}s–{r.End:F1}s",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                FontSize = 11,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, offsetX + r.Rx * job.Width * scale);
            Canvas.SetTop(label, offsetY + r.Ry * job.Height * scale - 16);
            BoxCanvas.Children.Add(label);
        }
    }

    // ---- Drawing a manual box ----

    private void BoxCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.DrawMode) return;
        _drawStart = e.GetPosition(BoxCanvas);
        _drawBox = new Border
        {
            BorderBrush = Brushes.Lime,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(40, 0, 255, 0)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_drawBox, _drawStart.Value.X);
        Canvas.SetTop(_drawBox, _drawStart.Value.Y);
        BoxCanvas.Children.Add(_drawBox);
        BoxCanvas.CaptureMouse();
    }

    private void BoxCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drawStart == null || _drawBox == null) return;
        var p = e.GetPosition(BoxCanvas);
        double x = Math.Min(_drawStart.Value.X, p.X);
        double y = Math.Min(_drawStart.Value.Y, p.Y);
        double w = Math.Abs(p.X - _drawStart.Value.X);
        double h = Math.Abs(p.Y - _drawStart.Value.Y);
        Canvas.SetLeft(_drawBox, x);
        Canvas.SetTop(_drawBox, y);
        _drawBox.Width = w;
        _drawBox.Height = h;
    }

    private void BoxCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawStart == null || _drawBox == null) return;
        var p = e.GetPosition(BoxCanvas);
        var job = _vm.CurrentJob;
        BoxCanvas.ReleaseMouseCapture();
        double x = Math.Min(_drawStart.Value.X, p.X);
        double y = Math.Min(_drawStart.Value.Y, p.Y);
        double w = Math.Abs(p.X - _drawStart.Value.X);
        double h = Math.Abs(p.Y - _drawStart.Value.Y);
        _drawStart = null;
        _drawBox = null;
        RenderBoxes();
        if (job == null || w < 4 || h < 4) { _vm.DrawMode = false; return; }

        // Convert canvas pixels back to relative video coords.
        var (scale, offsetX, offsetY) = VideoTransform();
        double rx = (x - offsetX) / (job.Width * scale);
        double ry = (y - offsetY) / (job.Height * scale);
        double rw = w / (job.Width * scale);
        double rh = h / (job.Height * scale);
        rx = Math.Clamp(rx, 0, 1);
        ry = Math.Clamp(ry, 0, 1);
        rw = Math.Clamp(rw, 0, 1 - rx);
        rh = Math.Clamp(rh, 0, 1 - ry);
        _vm.AddManualRegion(rx, ry, rw, rh);
        _vm.DrawMode = false; // box drawn — back to playback mode so Play works right away
    }

    // ---- Jobs / fullscreen / drag-drop ----

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobList.SelectedItem is Job job && job != _vm.CurrentJob)
            _ = _vm.SelectJobAsync(job);
    }

    private void RegionList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedRegion != null)
            SeekTo(_vm.SelectedRegion.Start);
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        // The OpenFileDialog lives in the ViewModel command; this button
        // just invokes it so the header stays in XAML.
        _vm.OpenVideoCommand.Execute(null);
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _savedState = WindowState;
            _savedStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
        else
        {
            WindowStyle = _savedStyle;
            WindowState = _savedState;
            _isFullscreen = false;
        }
        RenderBoxes(); // entering/leaving fullscreen changes the size, so the overlay is refit
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var video = files.FirstOrDefault(f =>
            f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        if (video != null)
            _ = _vm.OpenVideoAsync(video);
        else
            _vm.StatusMessage = "Only .mp4, .mov and .mkv videos can be opened.";
    }
}
