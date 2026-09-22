using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Downlism.Core.Media;
using Xunit;

namespace Downlism.Tests;

public sealed class MediaToolsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Downlism-provision-" + Guid.NewGuid());

    [Fact]
    public async Task CleanInstallProvisionsRuntimeBeforeProbeAndFfmpegBeforeDownload()
    {
        using var handler = new ToolServer();
        using var client = new HttpClient(handler);
        var tools = new MediaTools(client, _directory);
        Assert.False(tools.IsReady);
        var notes = new List<string>();
        await tools.EnsureYtDlpAsync(notes.Add, null, CancellationToken.None);
        Assert.True(File.Exists(tools.YtDlpPath));
        Assert.Equal("deno.exe", await File.ReadAllTextAsync(tools.DenoPath));
        Assert.False(File.Exists(tools.FfmpegPath));
        Assert.Contains(notes, note => note.Contains("Deno"));
        await tools.EnsureAsync(notes.Add, null, CancellationToken.None);
        Assert.True(tools.IsReady);
        Assert.True(File.Exists(Path.Combine(_directory, "ffprobe.exe")));
        var requests = handler.Requests;
        await tools.EnsureAsync(null, null, CancellationToken.None);
        Assert.Equal(requests, handler.Requests);
    }

    [Fact]
    public async Task ExistingInstallAutomaticallyRepairsMissingRuntimeAndFfprobe()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "yt-dlp.exe"), "installed");
        await File.WriteAllTextAsync(Path.Combine(_directory, "ffmpeg.exe"), "installed");
        using var client = new HttpClient(new ToolServer());
        var tools = new MediaTools(client, _directory);
        await tools.EnsureAsync(null, null, CancellationToken.None);
        Assert.True(tools.IsReady);
        Assert.Equal("installed", await File.ReadAllTextAsync(tools.YtDlpPath));
    }

    [Fact]
    public async Task ConcurrentFirstUseSharesProvisioning()
    {
        using var handler = new ToolServer();
        using var client = new HttpClient(handler);
        var tools = new MediaTools(client, _directory);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => tools.EnsureAsync(null, null, CancellationToken.None)));
        Assert.True(tools.IsReady);
        // One probe and one transfer for each of the three small fixture assets.
        Assert.Equal(6, handler.Requests);
    }

    [Fact]
    public async Task FailedRuntimeInstallCanBeRetried()
    {
        using var handler = new ToolServer { FailRuntime = true };
        using var client = new HttpClient(handler);
        var tools = new MediaTools(client, _directory);
        await Assert.ThrowsAsync<HttpRequestException>(() => tools.EnsureYtDlpAsync(null, null, CancellationToken.None));
        Assert.False(File.Exists(tools.DenoPath));
        Assert.False(tools.IsReady);
        handler.FailRuntime = false;
        await tools.EnsureAsync(null, null, CancellationToken.None);
        Assert.True(tools.IsReady);
    }

    private sealed class ToolServer : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool FailRuntime { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Yield();
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (FailRuntime && path.Contains("deno")) return new(HttpStatusCode.ServiceUnavailable);
            var bytes = path.EndsWith("yt-dlp.exe") ? "yt-dlp"u8.ToArray()
                : path.Contains("deno") ? Archive("deno.exe") : Archive("bin/ffmpeg.exe", "bin/ffprobe.exe");
            var range = request.Headers.Range?.Ranges.Single();
            var start = (int)(range?.From ?? 0);
            var end = (int)(range?.To ?? bytes.Length - 1);
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(bytes[start..(end + 1)]) };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            return response;
        }

        private static byte[] Archive(params string[] names)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var name in names)
                {
                    var entry = zip.CreateEntry(name);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(Path.GetFileName(name));
                }
            return stream.ToArray();
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
