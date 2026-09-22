using Downlism.Core.Media;
using Xunit;

namespace Downlism.Tests;

public sealed class ToolDownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Downlism-tools-" + Guid.NewGuid());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FetchesToolsWithRangesWhenSupported(bool ranges)
    {
        var bytes = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        await using var server = new TestHttpServer(bytes) { SupportsRanges = ranges };
        using var client = new HttpClient();
        await ToolDownload.FetchAsync(client, server.Uri, _directory, "tool.exe", null, CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "tool.exe")));
        if (ranges) Assert.True(server.RangeRequests.Count(range => range.End > range.Start) > 1);
    }

    [Fact]
    public async Task FailedRefreshPreservesInstalledTool()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "tool.exe");
        await File.WriteAllTextAsync(path, "working tool");
        using var client = new HttpClient(new UnavailableServer());
        await Assert.ThrowsAsync<HttpRequestException>(() => ToolDownload.FetchAsync(
            client, new Uri("https://example.test/tool.exe"), _directory, "tool.exe", null, CancellationToken.None));
        Assert.Equal("working tool", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InterruptedToolTransferResumesAutomatically()
    {
        var bytes = new byte[1024 * 1024];
        new Random(42).NextBytes(bytes);
        await using var server = new TestHttpServer(bytes) { TruncateAfterBytes = 4096 };
        using var client = new HttpClient();
        await ToolDownload.FetchAsync(client, server.Uri, _directory, "tool.exe", null, CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "tool.exe")));
        Assert.True(server.RangeRequests.Count(range => range.Start == 0 && range.End == 0) > 1);
    }

    private sealed class UnavailableServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
