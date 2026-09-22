using Downlism.Core.Media;
using Xunit;

namespace Downlism.Tests;

public sealed class MediaFormatsTests
{
    [Fact]
    public void ReadsOnlyQualitiesTheSourceActuallyOffers()
    {
        const string json = """
            {
              "formats": [
                { "format_id": "audio-low", "vcodec": "none", "acodec": "opus", "abr": 64.2 },
                { "format_id": "audio-high", "vcodec": "none", "acodec": "opus", "abr": 129.6 },
                { "format_id": "video-720", "vcodec": "avc1", "acodec": "none", "height": 720 },
                { "format_id": "video-1080", "vcodec": "avc1", "acodec": "none", "height": 1080 },
                { "format_id": "combined", "vcodec": "avc1", "acodec": "mp4a", "height": 720, "tbr": 1800 }
              ]
            }
            """;

        var formats = MediaFormats.Parse(json);

        Assert.True(formats.HasVideo);
        Assert.True(formats.HasAudio);
        Assert.Equal([1080, 720], formats.VideoHeights);
        Assert.Equal([130, 64], formats.AudioBitrates);
    }

    [Fact]
    public void UsesTotalBitrateForAudioOnlyFormatsWhenAbrIsMissing()
    {
        const string json = """
            { "formats": [{ "vcodec": "none", "acodec": "aac", "tbr": 192.4 }] }
            """;

        var formats = MediaFormats.Parse(json);

        Assert.Equal([192], formats.AudioBitrates);
    }

    [Fact]
    public void AcceptsNullMetricsFromYoutubeMetadata()
    {
        var formats = MediaFormats.Parse("""
            { "formats": [
                { "vcodec": "none", "acodec": "opus", "height": null, "abr": null, "tbr": 128 },
                { "vcodec": "avc1", "acodec": "none", "height": 1080, "abr": null }
            ] }
            """);
        Assert.Equal([1080], formats.VideoHeights);
        Assert.Equal([128], formats.AudioBitrates);
    }

    [Fact]
    public void HandlesSourcesWithoutAFormatList()
    {
        var formats = MediaFormats.Parse("{ \"title\": \"unresolved\" }");

        Assert.False(formats.HasVideo);
        Assert.False(formats.HasAudio);
        Assert.Empty(formats.VideoHeights);
        Assert.Empty(formats.AudioBitrates);
    }
}
