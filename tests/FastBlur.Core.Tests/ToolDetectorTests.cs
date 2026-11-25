using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Models;

namespace FastBlur.Core.Tests;

public sealed class ToolDetectorTests : IDisposable
{
    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "fastblur-test-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private string ExeFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "fake");
        return path;
    }

    [Fact]
    public void ResolveTool_ConfigPath_Wins()
    {
        var cfgPath = ExeFile(_tmp, "ffmpeg.exe");
        var found = ToolDetector.ResolveTool("ffmpeg", cfgPath, "FASTBLUR_FFMPEG_TEST_X", _tmp, _tmp, _ => null, out _);
        Assert.Equal(cfgPath, found);
    }

    [Fact]
    public void ResolveTool_EnvVar_WinsOverExeDir()
    {
        var envPath = ExeFile(_tmp, "my-ffmpeg.exe");
        Environment.SetEnvironmentVariable("FASTBLUR_FFMPEG_TEST_Y", envPath);
        try
        {
            var found = ToolDetector.ResolveTool("ffmpeg", "", "FASTBLUR_FFMPEG_TEST_Y", _tmp, _tmp, _ => null, out _);
            Assert.Equal(envPath, found);
        }
        finally { Environment.SetEnvironmentVariable("FASTBLUR_FFMPEG_TEST_Y", null); }
    }

    [Fact]
    public void ResolveTool_ExeToolsFolder_IsSearched()
    {
        var toolsDir = Directory.CreateDirectory(Path.Combine(_tmp, "tools")).FullName;
        var expected = ExeFile(toolsDir, "ffmpeg.exe");
        var found = ToolDetector.ResolveTool("ffmpeg", "", "FASTBLUR_FFMPEG_TEST_Z", _tmp, _tmp, _ => null, out var searched);
        Assert.Equal(expected, found);
        Assert.Contains(Path.Combine(_tmp, "ffmpeg.exe"), searched);
        Assert.Contains(expected, searched);
    }

    [Fact]
    public void ResolveTool_PathFallback_AndNotFound()
    {
        var found = ToolDetector.ResolveTool("ffmpeg", "", "FASTBLUR_FFMPEG_TEST_W", _tmp, _tmp, _ => "/usr/bin/ffmpeg", out var searched);
        Assert.Equal("/usr/bin/ffmpeg", found);
        Assert.Contains("PATH: /usr/bin/ffmpeg", searched);

        var missing = ToolDetector.ResolveTool("no-such-tool", "", "FASTBLUR_FFMPEG_TEST_W", _tmp, _tmp, _ => null, out var searched2);
        Assert.Equal("", missing);
        Assert.Contains("PATH: no-such-tool (not found)", searched2);
    }

    [Fact]
    public void FindTessData_PointsAtTessdataDir()
    {
        var tessdata = Directory.CreateDirectory(Path.Combine(_tmp, "tools", "tessdata")).FullName;
        File.WriteAllText(Path.Combine(tessdata, "eng.traineddata"), "fake");
        var found = ToolDetector.FindTessData(Path.Combine(_tmp, "tools", "tesseract.exe"), _tmp);
        Assert.Equal(tessdata, found);
    }

    [Fact]
    public void FindTessData_Missing_ReturnsEmpty()
    {
        Assert.Equal("", ToolDetector.FindTessData("", _tmp));
    }

    [Fact]
    public void DetectFrom_FindsToolsNextToExe()
    {
        var toolsDir = Directory.CreateDirectory(Path.Combine(_tmp, "tools")).FullName;
        ExeFile(toolsDir, "ffmpeg.exe");
        ExeFile(toolsDir, "ffprobe.exe");
        ExeFile(toolsDir, "tesseract.exe");
        var tessdata = Directory.CreateDirectory(Path.Combine(toolsDir, "tessdata")).FullName;
        File.WriteAllText(Path.Combine(tessdata, "eng.traineddata"), "fake");

        var tools = ToolDetector.DetectFrom(new AppConfig(), _tmp, _tmp, _ => null);
        Assert.True(tools.FFmpegOk);
        Assert.Equal(Path.Combine(toolsDir, "ffmpeg.exe"), tools.FFmpeg);
        // ffprobe is preferred from ffmpeg's own folder.
        Assert.Equal(Path.Combine(toolsDir, "ffprobe.exe"), tools.FFprobe);
        Assert.True(tools.TesseractOk);
        Assert.Equal(tessdata, tools.TessData);
    }

    [Fact]
    public void CandidateDirs_PriorityOrder()
    {
        var dirs = ToolDetector.CandidateDirs("/exe", "/cwd");
        Assert.Equal("/exe", dirs[0]);
        Assert.Equal(Path.Combine("/exe", "tools"), dirs[1]);
        Assert.Equal(Path.Combine("/exe", "bin"), dirs[2]);
        Assert.Equal("/cwd", dirs[3]);
        Assert.Equal(Path.Combine("/cwd", "tools"), dirs[4]);
    }
}
