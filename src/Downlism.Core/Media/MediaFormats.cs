using System.Text.Json;

namespace Downlism.Core.Media;

/// <summary>The qualities yt-dlp reported for one media page.</summary>
public sealed record MediaFormats(
    IReadOnlyList<int> VideoHeights,
    IReadOnlyList<int> AudioBitrates,
    bool HasVideo,
    bool HasAudio)
{
    public static MediaFormats Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("formats", out var formats)
            || formats.ValueKind != JsonValueKind.Array)
        {
            return new MediaFormats([], [], HasVideo: false, HasAudio: false);
        }

        var heights = new HashSet<int>();
        var bitrates = new HashSet<int>();
        var anyVideo = false;
        var anyAudio = false;

        foreach (var format in formats.EnumerateArray())
        {
            var hasVideo = Codec(format, "vcodec");
            var hasAudio = Codec(format, "acodec");
            anyVideo |= hasVideo;
            anyAudio |= hasAudio;

            var height = Number(format, "height");
            if (hasVideo && height is > 0)
            {
                heights.Add((int)Math.Round(height.Value));
            }

            // abr describes the audio stream itself. tbr is only a useful fallback for an
            // audio-only format; on a combined format it also includes the video bitrate.
            var bitrate = Number(format, "abr");
            if (bitrate is null && hasAudio && !hasVideo) bitrate = Number(format, "tbr");
            if (hasAudio && bitrate is > 0)
            {
                bitrates.Add((int)Math.Round(bitrate.Value));
            }
        }

        return new MediaFormats(
            heights.OrderDescending().ToArray(),
            bitrates.OrderDescending().ToArray(),
            anyVideo,
            anyAudio);
    }

    private static bool Codec(JsonElement format, string name) =>
        format.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } codec
        && !string.Equals(codec, "none", StringComparison.OrdinalIgnoreCase);

    private static double? Number(JsonElement format, string name) =>
        format.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
}
