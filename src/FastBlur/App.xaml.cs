using System.IO;
using System.Windows;
using FastBlur.Core.Cli;
using FastBlur.Core.Config;
using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Jobs;
using FastBlur.ViewModels;

namespace FastBlur;

/// <summary>
/// App startup: CLI mode (scan / blur / --version / --help) runs headless
/// and exits; otherwise the WPF window opens. No web server, no browser.
/// </summary>
public partial class App : Application
{
    public const string Version = "0.1.0";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDir = AppConfigStore.DefaultDataDir();
        var cfg = AppConfigStore.Load(dataDir);
        var tools = ToolDetector.Detect(cfg);
        var store = await JobStore.OpenAsync(dataDir, tools);

        // Headless CLI: fastblur scan <video> --out detections.json
        //              fastblur blur <video> --regions regions.json --out out.mp4
        if (e.Args.Length > 0)
        {
            int code = await CliRunner.RunAsync(e.Args, dataDir, cfg, tools, store);
            Shutdown(code);
            return;
        }

        var vm = new MainViewModel(dataDir, cfg, tools, store);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
    }
}
