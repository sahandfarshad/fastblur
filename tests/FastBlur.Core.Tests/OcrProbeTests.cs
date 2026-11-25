using FastBlur.Core.Ffmpeg;
using FastBlur.Core.Ocr;

namespace FastBlur.Core.Tests;

public sealed class TesseractRunnerTests
{
    [Fact]
    public void ParseTsv_OnlyLevelFiveWords_WithConfAtLeast20()
    {
        var tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "5\t1\t1\t1\t1\t1\t10\t20\t50\t12\t95.000000\tpassword:\n"
            + "5\t1\t1\t1\t1\t2\t70\t20\t40\t12\t88.500000\thunter2\n"
            + "5\t1\t1\t1\t1\t3\t120\t20\t30\t12\t10.000000\tnoisy\n"   // conf < 20: dropped
            + "4\t1\t1\t1\t1\t0\t10\t20\t120\t12\t90.000000\tpassword: hunter2\n" // level 4: dropped
            + "5\t1\t1\t1\t1\t4\t160\t20\t10\t12\t90.000000\t\n"; // empty text: dropped
        var words = TesseractRunner.ParseTsv(tsv);
        Assert.Equal(2, words.Count);
        Assert.Equal("password:", words[0].Text);
        Assert.Equal(10, words[0].X);
        Assert.Equal(20, words[0].Y);
        Assert.Equal(50, words[0].W);
        Assert.Equal(95, words[0].Conf);
        Assert.Equal("hunter2", words[1].Text);
        Assert.Equal(88, words[1].Conf); // float confidence truncated to int
    }

    [Fact]
    public void ParseTsv_Empty_ReturnsEmpty()
    {
        Assert.Empty(TesseractRunner.ParseTsv(""));
        Assert.Empty(TesseractRunner.ParseTsv("level\tpage_num\n"));
    }

    [Theory]
    [InlineData(1920, 2.0)]   // 1080p: 3840/1920 = 2, capped at 2
    [InlineData(1280, 2.0)]   // 3840/1280 = 3, capped at 2
    [InlineData(2560, 1.0)]   // 4K-ish: already big enough
    [InlineData(3840, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(2000, 1.92)]  // 3840/2000 = 1.92
    public void OcrScaleFactor(int frameW, double expected)
    {
        Assert.Equal(expected, TesseractRunner.OcrScaleFactor(frameW), precision: 3);
    }

    [Fact]
    public void ScaleWordsToOriginal_HalvesAt2x()
    {
        var words = new[]
        {
            new Core.Detect.OcrWord { Text = "a", X = 100, Y = 50, W = 40, H = 24, Conf = 90 },
        };
        var scaled = TesseractRunner.ScaleWordsToOriginal(words, 2);
        Assert.Equal(50, scaled[0].X);
        Assert.Equal(25, scaled[0].Y);
        Assert.Equal(20, scaled[0].W);
        Assert.Equal(12, scaled[0].H);
    }

    [Fact]
    public void ScaleWordsToOriginal_FactorOne_ReturnsSame()
    {
        var words = new[] { new Core.Detect.OcrWord { Text = "a", X = 10, Y = 10, W = 5, H = 5, Conf = 90 } };
        Assert.Equal(10, TesseractRunner.ScaleWordsToOriginal(words, 1)[0].X);
    }
}

public sealed class VideoProbeTests
{
    [Fact]
    public void ParseFFprobeJson_ReadsDurationSizeFpsCodecFormat()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 1279, "height": 719, "r_frame_rate": "30/1" },
                { "codec_type": "audio", "codec_name": "aac" }
              ],
              "format": { "duration": "123.456", "format_name": "matroska,webm" }
            }
            """;
        var info = VideoProbe.ParseFFprobeJson(json);
        Assert.Equal(123.456, info.Duration, precision: 3);
        Assert.Equal(1279, info.Width);
        Assert.Equal(719, info.Height);
        Assert.Equal(30, info.Fps);
        Assert.Equal("h264", info.Codec);
        Assert.Equal("matroska", info.Format);
    }

    [Fact]
    public void ParseFFmpegStderr_ReadsSameFields()
    {
        const string stderr = """
            Input #0, matroska, from 'recording.mkv':
              Duration: 00:02:03.45, start: 0.000000, bitrate: 5000 kb/s
              Stream #0:0: Video: h264 (High), yuv420p, 1279x719, 30 fps, 30 tbr, 1k tbn
              Stream #0:1: Audio: opus, 48000 Hz, stereo
            """;
        var info = VideoProbe.ParseFFmpegStderr(stderr);
        Assert.Equal(123.45, info.Duration, precision: 2);
        Assert.Equal("matroska", info.Format);
        Assert.Equal("h264", info.Codec);
        Assert.Equal(1279, info.Width);
        Assert.Equal(719, info.Height);
        Assert.Equal(30, info.Fps);
    }

    [Fact]
    public void NormalizeProbe_UsesFileExtension()
    {
        var info = VideoProbe.NormalizeProbe("/tmp/clip.mov",
            new Core.Models.VideoInfo { Format = "mov,mp4,m4a", Duration = 10, Width = 100, Height = 100 });
        Assert.Equal("mov", info.Format);
    }
}
