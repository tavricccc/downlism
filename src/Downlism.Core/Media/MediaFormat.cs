using System.Globalization;
using Downlism.Core.Downloads;

namespace Downlism.Core.Media;

/// <summary>Turns the media choices shown in the download prompt into yt-dlp arguments.</summary>
public static class MediaFormat
{
    public static IReadOnlyList<string> ArgumentsFor(DownloadRequest request)
    {
        if (request.MediaOutput == MediaOutput.Audio)
        {
            return
            [
                "-f", "bestaudio/best",
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", request.MediaQuality is > 0
                    ? request.MediaQuality.Value.ToString(CultureInfo.InvariantCulture) + "K"
                    : "0",
            ];
        }

        var selector = request.MediaQuality is > 0
            ? $"bestvideo[height<={request.MediaQuality.Value}]+bestaudio/best[height<={request.MediaQuality.Value}]"
            : "bestvideo+bestaudio/best";

        return ["-f", selector, "--merge-output-format", "mp4"];
    }
}
