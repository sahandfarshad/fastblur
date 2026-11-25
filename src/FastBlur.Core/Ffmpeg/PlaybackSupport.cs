using FastBlur.Core.Models;

namespace FastBlur.Core.Ffmpeg;

/// <summary>
/// What WPF's MediaElement can play directly, without a converted preview.
/// MediaElement plays mp4/m4v/wmv with h264 — it does NOT play .mkv, .mov,
/// or HEVC/h265, even though ffmpeg reads them fine. Those need a converted
/// preview (see <see cref="Exporter.MakePreviewAsync"/>). Odd dimensions are
/// fine for a player (unlike libx264), so only container/codec decide.
/// Format is the file extension — ffprobe calls both .mp4 and .mov
/// "mov,mp4,...", so the extension is the reliable container.
/// </summary>
public static class PlaybackSupport
{
    public static bool IsDirectlyPlayable(VideoInfo info)
    {
        switch (info.Format.ToLowerInvariant())
        {
            case "mp4":
            case "m4v":
            case "wmv":
                break;
            default:
                return false;
        }
        switch (info.Codec.ToLowerInvariant())
        {
            case "h264":
            case "avc":
            case "wmv3":
            case "vc1":
                return true;
            default:
                return false;
        }
    }
}
