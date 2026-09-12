using System.Diagnostics;
using System.Security.Cryptography;
using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Xunit;

namespace Downlism.Tests;

public sealed class DownloadEngineTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("downlism-engine").FullName;
    private readonly HttpClient _client = DownloadHttpClientFactory.Create(16);

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        // Deterministic but position-dependent, so a segment written at the wrong offset
        // changes the hash instead of hiding inside a block of identical bytes.
        for (var index = 0; index < length; index++) bytes[index] = (byte)(index * 31 % 251);
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public async Task DownloadsSegmentedFileByteForByte()
    {
        var payload = Payload(3 * 1024 * 1024);
        await using var server = new TestHttpServer(payload);

        var result = await new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory, Connections = 8 },
            progress: null,
            CancellationToken.None);

        Assert.Equal(payload.Length, result.Bytes);
        Assert.Equal(Hash(payload), Hash(await File.ReadAllBytesAsync(result.Path)));
        Assert.Equal("payload.bin", Path.GetFileName(result.Path));

        // The one-byte probe plus one request per segment.
        Assert.True(server.RangeRequests.Count > 1, "expected the transfer to be segmented");
        Assert.Contains(server.RangeRequests, request => request is { Start: 0, End: 0 });
    }

    [Fact]
    public async Task LeavesNoPartialFileOrSidecarBehind()
    {
        await using var server = new TestHttpServer(Payload(2 * 1024 * 1024));

        var result = await new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory },
            progress: null,
            CancellationToken.None);

        Assert.False(File.Exists(result.Path + DownloadTarget.PartialExtension));
        Assert.False(File.Exists(SegmentStateFile.PathFor(result.Path + DownloadTarget.PartialExtension)));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ResumesFromSidecarAfterAFailedAttempt()
    {
        var payload = Payload(4 * 1024 * 1024);
        await using var server = new TestHttpServer(payload);

        // Cut one segment short so the first attempt fails with progress already on disk.
        server.TruncateAfterBytes = 64 * 1024;
        var engine = new DownloadEngine(_client);
        var request = new DownloadRequest { Uri = server.Uri, Directory = _directory, Connections = 4 };

        await Assert.ThrowsAnyAsync<Exception>(() => engine.RunAsync(request, null, CancellationToken.None));

        var partial = Path.Combine(_directory, "payload.bin" + DownloadTarget.PartialExtension);
        Assert.True(File.Exists(partial), "the partial file should survive a failed attempt");

        using (var state = SegmentStateFile.TryOpen(SegmentStateFile.PathFor(partial)))
        {
            Assert.NotNull(state);
            Assert.True(state.CompletedBytes() > 0, "progress should have been checkpointed");
            Assert.False(state.IsComplete());
        }

        var result = await engine.RunAsync(request, null, CancellationToken.None);

        Assert.Equal(Hash(payload), Hash(await File.ReadAllBytesAsync(result.Path)));
    }

    [Fact]
    public async Task RestartsWhenTheRemoteFileChangedBetweenAttempts()
    {
        await using var server = new TestHttpServer(Payload(4 * 1024 * 1024));
        var engine = new DownloadEngine(_client);
        var request = new DownloadRequest { Uri = server.Uri, Directory = _directory, Connections = 4 };

        server.TruncateAfterBytes = 64 * 1024;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.RunAsync(request, null, CancellationToken.None));

        // A different entity behind the same URL must not be merged with the old partial data.
        var replacement = Payload(4 * 1024 * 1024 + 5000);
        server.Content = replacement;
        server.ETag = "\"v2\"";

        var result = await engine.RunAsync(request, null, CancellationToken.None);

        Assert.Equal(Hash(replacement), Hash(await File.ReadAllBytesAsync(result.Path)));
    }

    [Fact]
    public async Task FallsBackToOneConnectionWhenTheServerRefusesRanges()
    {
        var payload = Payload(1024 * 1024);
        await using var server = new TestHttpServer(payload) { SupportsRanges = false };

        var result = await new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory, Connections = 8 },
            progress: null,
            CancellationToken.None);

        Assert.Equal(Hash(payload), Hash(await File.ReadAllBytesAsync(result.Path)));
        // Only the probe asked for a range; nothing else tried to.
        Assert.Single(server.RangeRequests);
    }

    [Fact]
    public async Task PrefersTheNameFromContentDisposition()
    {
        await using var server = new TestHttpServer(Payload(4096))
        {
            ContentDisposition = "attachment; filename*=UTF-8''%E5%A0%B1%E5%91%8A.bin",
        };

        var result = await new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory },
            progress: null,
            CancellationToken.None);

        Assert.Equal("報告.bin", Path.GetFileName(result.Path));
    }

    [Fact]
    public async Task SortsIntoACategoryFolderUsingTheResolvedName()
    {
        // The URL says .zip and the response says .exe. The category has to follow the response,
        // because that is what the file on disk will actually be.
        await using var server = new TestHttpServer(Payload(4096))
        {
            ContentDisposition = "attachment; filename=\"setup.exe\"",
        };

        var result = await new DownloadEngine(_client).RunAsync(
            new DownloadRequest
            {
                Uri = new Uri($"http://127.0.0.1:{server.Port}/bundle.zip"),
                Directory = _directory,
                SortIntoCategories = true,
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "程式", "setup.exe"), result.Path);
    }

    [Fact]
    public async Task KeepsTheFileInTheRootWhenSortingIsOff()
    {
        await using var server = new TestHttpServer(Payload(4096))
        {
            ContentDisposition = "attachment; filename=\"clip.mp4\"",
        };

        var result = await new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory, SortIntoCategories = false },
            progress: null,
            CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "clip.mp4"), result.Path);
    }

    [Fact]
    public async Task KeepsBothFilesWhenTheNameIsAlreadyTaken()
    {
        await using var server = new TestHttpServer(Payload(4096));
        var engine = new DownloadEngine(_client);
        var request = new DownloadRequest { Uri = server.Uri, Directory = _directory };

        var first = await engine.RunAsync(request, null, CancellationToken.None);
        var second = await engine.RunAsync(request, null, CancellationToken.None);

        Assert.Equal("payload.bin", Path.GetFileName(first.Path));
        Assert.Equal("payload (2).bin", Path.GetFileName(second.Path));
    }

    [Fact]
    public async Task MarksTheFileAsInternetSourced()
    {
        await using var server = new TestHttpServer(Payload(4096));

        var result = await new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory },
            progress: null,
            CancellationToken.None);

        var zone = await File.ReadAllTextAsync(result.Path + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsProgressThatEndsAtTheFullSize()
    {
        var payload = Payload(2 * 1024 * 1024);
        await using var server = new TestHttpServer(payload);

        var reports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(report =>
        {
            lock (reports) reports.Add(report);
        });

        await new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory, Connections = 4 },
            progress,
            CancellationToken.None);

        // Progress<T> posts asynchronously, so allow the queued callbacks to drain.
        await Task.Delay(200);

        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.Equal(payload.Length, reports.Max(report => report.CompletedBytes));
            Assert.All(reports, report => Assert.Equal(payload.Length, report.TotalBytes));
        }
    }

    [Fact]
    public async Task CancellationStopsTheTransfer()
    {
        await using var server = new TestHttpServer(Payload(8 * 1024 * 1024));
        using var cancellation = new CancellationTokenSource();

        var running = new DownloadEngine(_client).RunAsync(
            new DownloadRequest { Uri = server.Uri, Directory = _directory, Connections = 4, BytesPerSecond = 32 * 1024 },
            progress: null,
            cancellation.Token);

        await cancellation.CancelAsync();

        // Always a cancellation, never the IOException that tearing down a socket mid-read can
        // otherwise produce: the app decides between "paused" and "failed" on this distinction.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task CancellationMidTransferIsStillReportedAsCancellation()
    {
        await using var server = new TestHttpServer(Payload(16 * 1024 * 1024));
        using var cancellation = new CancellationTokenSource();

        var running = new DownloadEngine(_client).RunAsync(
            new DownloadRequest
            {
                Uri = server.Uri,
                Directory = _directory,
                Connections = 8,
                // Slow enough that the transfer is still reading bodies when it is cancelled;
                // over loopback an unthrottled 16 MB finishes before the delay below elapses.
                BytesPerSecond = 512 * 1024,
            },
            progress: null,
            cancellation.Token);

        // Let the connections get as far as reading bodies before pulling the rug out.
        await Task.Delay(150);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RateLimitHoldsTheTransferToRoughlyTheConfiguredSpeed()
    {
        var payload = Payload(512 * 1024);
        await using var server = new TestHttpServer(payload);

        var started = Stopwatch.GetTimestamp();
        await new DownloadEngine(_client).RunAsync(
            new DownloadRequest
            {
                Uri = server.Uri,
                Directory = _directory,
                Connections = 4,
                BytesPerSecond = 256 * 1024,
            },
            progress: null,
            CancellationToken.None);

        // 512 KiB at 256 KiB/s takes about two seconds, less the one-second burst allowance.
        // The assertion is deliberately loose: it catches a limiter that does nothing at all
        // without turning ordinary scheduling jitter into a failing test.
        Assert.True(Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(700),
            "the rate limiter did not slow the transfer down");
    }

    public void Dispose()
    {
        _client.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
