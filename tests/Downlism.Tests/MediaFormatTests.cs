using Downlism.Core.Downloads;
using Downlism.Core.Media;
using Xunit;

namespace Downlism.Tests;

public sealed class MediaFormatTests
{
    [Fact]
    public void BestVideoDownloadsAndMergesSeparateStreams()
    {
        var arguments = MediaFormat.ArgumentsFor(Request());

        Assert.Equal(["-f", "bestvideo+bestaudio/best", "--merge-output-format", "mp4"], arguments);
    }

    [Fact]
    public void VideoQualitySelectsTheNearestAvailableResolution()
    {
        var arguments = MediaFormat.ArgumentsFor(Request() with { MediaQuality = 1080 });

        Assert.Equal(
            ["-f", "bestvideo+bestaudio/best", "--format-sort", "res~1080", "--merge-output-format", "mp4"],
            arguments);
    }

    [Fact]
    public void AudioChoiceExtractsMp3AtSelectedBitrate()
    {
        var arguments = MediaFormat.ArgumentsFor(Request() with
        {
            MediaOutput = MediaOutput.Audio,
            MediaQuality = 192,
        });

        Assert.Equal(
            ["-f", "bestaudio/best", "--format-sort", "abr~192", "--extract-audio", "--audio-format", "mp3", "--audio-quality", "192K"],
            arguments);
    }

    [Fact]
    public void BestAudioUsesFfmpegsHighestQualitySetting()
    {
        var arguments = MediaFormat.ArgumentsFor(Request() with { MediaOutput = MediaOutput.Audio });

        Assert.Equal("0", arguments[^1]);
    }

    private static DownloadRequest Request() => new()
    {
        Uri = new Uri("https://example.com/watch/1"),
        Kind = TransferKind.Media,
        Directory = @"C:\Downloads",
    };
}
