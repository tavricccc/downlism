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
            var arguments = new List<string> { "-f", "bestaudio/best" };
            if (request.MediaQuality is > 0)
            {
                // A page can change its formats between probing and starting. Sorting by the
                // nearest bitrate still returns a valid stream when the exact one disappeared.
                arguments.AddRange(["--format-sort", "abr~" + request.MediaQuality.Value.ToString(CultureInfo.InvariantCulture)]);
            }

            arguments.AddRange(
            [
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", request.MediaQuality is > 0
                    ? request.MediaQuality.Value.ToString(CultureInfo.InvariantCulture) + "K"
                    : "0",
            ]);
            return arguments;
        }

        var video = new List<string> { "-f", "bestvideo+bestaudio/best" };
        if (request.MediaQuality is > 0)
        {
            // '~' asks yt-dlp for the numerically nearest available resolution, so a format
            // disappearing after the probe does not turn a valid download into an error.
            video.AddRange(["--format-sort", "res~" + request.MediaQuality.Value.ToString(CultureInfo.InvariantCulture)]);
        }

        video.AddRange(["--merge-output-format", "mp4"]);
        return video;
    }
}
