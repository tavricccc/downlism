using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Xunit;

namespace Downlism.Tests;

public sealed class DownloadSafetyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("downlism-safety").FullName;
    private DownloadRequest Request => new() { Uri = new("https://origin.example/file.bin"), Directory = _directory, Connections = 1 };
    private static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes, string? range = null, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        if (range is not null) response.Content.Headers.TryAddWithoutValidation("Content-Range", range);
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }
    [Fact]
    public async Task ProbeSendsCredentialsButDoesNotLeakCookiesAcrossRedirects()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(message =>
        {
            calls++;
            Assert.Equal("test-agent", message.Headers.UserAgent.ToString());
            if (message.RequestUri!.Host == "origin.example")
            {
                Assert.Equal("session=private", message.Headers.GetValues("Cookie").Single());
                var response = Response(HttpStatusCode.Found, []);
                response.Headers.Location = new Uri("https://cdn.example/file.bin");
                return response;
            }
            Assert.False(message.Headers.Contains("Cookie"));
            return Response(HttpStatusCode.PartialContent, [1], "bytes 0-0/10");
        }));
        var probe = await new DownloadProbe(client).ProbeAsync(Request with { Cookies = "session=private", UserAgent = "test-agent" }, default);
        Assert.Equal("cdn.example", probe.FinalUri.Host);
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task RejectsHttpsDowngrade()
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = Response(HttpStatusCode.Found, []);
            response.Headers.Location = new Uri("http://origin.example/file.bin");
            return response;
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new DownloadProbe(client).ProbeAsync(Request, default));
    }
    [Theory]
    [InlineData("bytes 1-9/10", 9)]
    [InlineData("bytes 0-9/11", 10)]
    [InlineData("bytes 0-8/10", 9)]
    public async Task RejectsWrongSegmentRangeBeforePublishing(string range, int count)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => ++calls == 1
            ? Response(HttpStatusCode.PartialContent, [1], "bytes 0-0/10")
            : Response(HttpStatusCode.PartialContent, new byte[count], range)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadEngine(client).RunAsync(Request, null, default));
        Assert.False(File.Exists(Path.Combine(_directory, "file.bin")));
    }
    [Fact]
    public async Task RejectsTruncatedSegmentEvenWhenServerOmitsContentLength()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) return Response(HttpStatusCode.PartialContent, [1], "bytes 0-0/10");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(new NonSeekableStream([1, 2, 3])) };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-9/10");
            return response;
        }));
        await Assert.ThrowsAsync<EndOfStreamException>(() => new DownloadEngine(client).RunAsync(Request, null, default));
        Assert.False(File.Exists(Path.Combine(_directory, "file.bin")));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    public async Task DoesNotResumeWithoutStrongValidator(string? etag)
    {
        using var client = new HttpClient(new Handler(_ => Response(HttpStatusCode.PartialContent, [1], "bytes 0-0/10", etag)));
        var probe = await new DownloadProbe(client).ProbeAsync(Request, default);
        Assert.False(probe.SupportsRanges);
    }
    [Fact]
    public async Task PublishesEmptyFileFromUnsatisfiedZeroByteProbe()
    {
        using var client = new HttpClient(new Handler(_ => Response(HttpStatusCode.RequestedRangeNotSatisfiable, [], "bytes */0")));
        var result = await new DownloadEngine(client).RunAsync(Request, null, default);
        Assert.Equal(0, new FileInfo(result.Path).Length);
    }
    [Fact]
    public async Task VerifiesSha256BeforePublishing()
    {
        Assert.False(RetryPolicy.IsTransient(new InvalidDataException("checksum mismatch")));
        byte[] bytes = [1, 2, 3, 4];
        using var client = new HttpClient(new Handler(_ => Response(HttpStatusCode.OK, bytes)));
        var engine = new DownloadEngine(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunAsync(Request with { ExpectedSha256 = new string('0', 64) }, null, default));
        Assert.False(File.Exists(Path.Combine(_directory, "file.bin")));
        var result = await engine.RunAsync(Request with { ExpectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)) }, null, default);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
    }
    [Fact]
    public async Task MissingPartialNeverReusesACompleteSidecar()
    {
        byte[] bytes = [3, 1, 4, 1, 5, 9];
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Request.Uri.AbsoluteUri + "\n\"v1\"")));
        using (SegmentStateFile.Create(Path.Combine(_directory, "file.bin.download.dlstate"), bytes.Length, identity,
            [new Segment(0, bytes.Length - 1, bytes.Length)])) { }
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => ++calls == 1
            ? Response(HttpStatusCode.PartialContent, [3], "bytes 0-0/6")
            : Response(HttpStatusCode.PartialContent, bytes, "bytes 0-5/6")));
        var result = await new DownloadEngine(client).RunAsync(Request, null, default);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.Equal(2, calls);
    }
    [Fact]
    public void ConcurrentCheckpointsUseIndependentRecordBuffers()
    {
        var path = Path.Combine(_directory, "parallel.dlstate");
        var segments = SegmentPlanner.Plan(80_000, 8, minimumSegmentLength: 1);
        using (var state = SegmentStateFile.Create(path, 80_000, "tag", segments))
            Parallel.For(0, segments.Count, index => { for (var n = 1; n <= 1000; n++) state.Update(index, n); });
        using var reopened = SegmentStateFile.TryOpen(path);
        Assert.NotNull(reopened);
        for (var i = 0; i < segments.Count; i++) Assert.Equal(segments[i] with { Completed = 1000 }, reopened.Segments[i]);
    }
    [Fact]
    public void RejectsSidecarWithOverlappingRanges()
    {
        var path = Path.Combine(_directory, "bad.dlstate");
        using (SegmentStateFile.Create(path, 10, "tag", [new Segment(0, 5, 0), new Segment(5, 9, 0)])) { }
        Assert.Null(SegmentStateFile.TryOpen(path));
    }
    [Fact]
    public async Task MetadataLimitAlsoAppliesWithoutContentLength()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(new byte[100])) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedHttpDownload.ReadAsync(client, Request, 10, default));
    }
    [Fact]
    public async Task MetadataReadsFollowRelativeRedirectsWithinTheLimit()
    {
        using var client = new HttpClient(new Handler(message =>
        {
            if (message.RequestUri!.AbsolutePath == "/file.bin")
            {
                var response = Response(HttpStatusCode.Found, []);
                response.Headers.Location = new Uri("/actual", UriKind.Relative); return response;
            }
            return Response(HttpStatusCode.OK, [1, 2, 3]);
        }));
        Assert.Equal(new byte[] { 1, 2, 3 }, await BoundedHttpDownload.ReadAsync(client, Request, 10, default));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    { public override bool CanSeek => false; }
    public void Dispose() => Directory.Delete(_directory, true);
}
