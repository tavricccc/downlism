using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Downlism.Core.Ingest;
using Xunit;

namespace Downlism.Tests;

public class TransferRoutingTests
{
    [Theory]
    [InlineData("https://example.com/setup.exe", TransferKind.Http)]
    [InlineData("https://example.com/archive.zip?token=1", TransferKind.Http)]
    [InlineData("https://cdn.example.com/vod/master.m3u8", TransferKind.Media)]
    [InlineData("https://cdn.example.com/vod/index.mpd", TransferKind.Media)]
    [InlineData("https://www.youtube.com/watch?v=abc", TransferKind.Media)]
    [InlineData("https://youtu.be/abc", TransferKind.Media)]
    [InlineData("https://m.bilibili.com/video/BV1", TransferKind.Media)]
    [InlineData("https://example.com/ubuntu.torrent", TransferKind.Torrent)]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", TransferKind.Torrent)]
    public void RoutesLinksToTheRightEngine(string url, TransferKind expected)
    {
        Assert.Equal(expected, TransferRouting.For(new Uri(url)));
    }

    [Fact]
    public void DoesNotMatchAHostThatMerelyEndsWithTheSameLetters()
    {
        // notyoutube.com is a different site. Matching on a bare suffix would hand it to
        // yt-dlp and download the page instead of the file it was serving.
        Assert.Equal(TransferKind.Http, TransferRouting.For(new Uri("https://notyoutube.com/file.bin")));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("javascript:fetch('/steal')")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesSchemesNoEngineCanStartFrom(string? text)
    {
        // The extension is the only caller, and a page controls what it sends: a file:// URL
        // arriving here would turn Downlism into a way to read local files on a page's behalf.
        Assert.False(TransferRouting.TryParse(text, out _));
    }

    [Fact]
    public void ReadsTheDisplayNameOutOfAMagnetLink()
    {
        var uri = new Uri("magnet:?xt=urn:btih:0123456789abcdef&dn=Big+Buck+Bunny%202160p&tr=udp%3A%2F%2Ftr");
        Assert.Equal("Big Buck Bunny 2160p", TransferRouting.NameFromMagnet(uri));
    }

    [Fact]
    public void FallsBackWhenAMagnetCarriesNoName()
    {
        // A row labelled with forty hex characters tells the person nothing at all.
        var uri = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567");

        Assert.Null(TransferRouting.NameFromMagnet(uri));
        Assert.Equal("download", SuggestedFileName.FromUri(uri));
    }

    [Fact]
    public void NamesAMagnetLinkFromItsDisplayName()
    {
        var uri = new Uri("magnet:?xt=urn:btih:0123456789abcdef&dn=debian-13.iso");
        Assert.Equal("debian-13.iso", SuggestedFileName.FromUri(uri));
    }

    [Fact]
    public void LetsTheExtensionUpgradeAFileToMedia()
    {
        // Only the extension saw the response headers, so only it can tell a video stream from
        // a file the URL gives no hint about.
        var message = new IngestMessage { Url = "https://example.com/play?id=7", Kind = "media" };

        Assert.True(message.TryGetUri(out var uri));
        Assert.Equal(TransferKind.Media, message.KindFor(uri));
    }

    [Fact]
    public void DoesNotLetAPageDivertAnOrdinaryLinkIntoTheSwarm()
    {
        // A claimed kind is only ever honoured when the URL could support it; otherwise a page
        // could hand an arbitrary address to the BitTorrent session.
        var message = new IngestMessage { Url = "https://example.com/setup.exe", Kind = "torrent" };

        Assert.True(message.TryGetUri(out var uri));
        Assert.Equal(TransferKind.Http, message.KindFor(uri));
    }

    [Fact]
    public void LetsTheExtensionDowngradeMediaBackToAPlainFile()
    {
        // An .m3u8 that the extension saw served as an attachment really is a file to save.
        var message = new IngestMessage { Url = "https://example.com/playlist.m3u8", Kind = "file" };

        Assert.True(message.TryGetUri(out var uri));
        Assert.Equal(TransferKind.Http, message.KindFor(uri));
    }

    [Fact]
    public void AcceptsAMagnetLinkFromTheExtension()
    {
        var message = new IngestMessage { Url = "magnet:?xt=urn:btih:0123456789abcdef&dn=x", Kind = "torrent" };

        Assert.True(message.TryGetUri(out var uri));
        Assert.Equal(TransferKind.Torrent, message.KindFor(uri));
    }
}
