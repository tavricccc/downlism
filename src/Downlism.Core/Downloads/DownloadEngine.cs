using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Downlism.Core.Http;

namespace Downlism.Core.Downloads;

/// <summary>Runs one transfer from probe to published file.</summary>
public sealed class DownloadEngine(HttpClient client) : ITransferEngine
{
    private const int BufferSize = 128 * 1024;
    private const long CheckpointInterval = 1024 * 1024;

    /// <summary>
    /// A transfer is considered stalled when one read takes longer than this. The client has
    /// no overall timeout, because a legitimate large download may run for hours.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    public async Task<DownloadResult> RunAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var probe = await new DownloadProbe(client).ProbeAsync(request.Uri, cancellationToken).ConfigureAwait(false);

        // A content-encoded body cannot be segmented: range offsets address the compressed
        // stream, while the bytes that must reach disk are the decoded ones.
        var segmented = probe.SupportsRanges && !probe.IsEncoded && probe.TotalLength is > 0;
        var fileName = request.FileName ?? probe.FileName;
        var directory = DownloadCategory.DirectoryFor(request.Directory, fileName, request.SortIntoCategories);

        using var target = DownloadTarget.Open(directory, fileName, probe.TotalLength, resume: segmented);

        var statePath = SegmentStateFile.PathFor(target.PartialPath);
        var state = segmented ? OpenOrCreateState(statePath, probe, request.Connections) : null;

        try
        {
            var limiter = new RateLimiter(request.BytesPerSecond);
            var snapshot = state;
            var reporter = new ProgressReporter(
                progress,
                probe.TotalLength,
                state?.CompletedBytes() ?? 0,
                () => snapshot?.Segments.ToArray() ?? []);

            if (state is null)
            {
                await TransferWholeAsync(request, probe, target, limiter, reporter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TransferSegmentsAsync(request, probe, target, state, limiter, reporter, cancellationToken).ConfigureAwait(false);
            }

            reporter.Final();
            state?.Dispose();
            state = null;

            var path = target.Publish(probe.FinalUri);
            // The sidecar only means anything while the transfer is unfinished.
            TryDelete(statePath);

            return new DownloadResult(path, reporter.Completed, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Tearing down sockets mid-read surfaces as an IOException as often as it does a
            // cancellation. Both mean the same thing once the caller has cancelled, and the
            // caller needs to tell a pause the user asked for from a transfer that broke.
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            state?.Dispose();
        }
    }

    /// <summary>
    /// Reuses an interrupted transfer only when the server is still serving the same bytes.
    /// A changed validator or length means the remote file was replaced, and the partial data
    /// on disk belongs to a file that no longer exists.
    /// </summary>
    private static SegmentStateFile OpenOrCreateState(string path, ProbeResult probe, int connections)
    {
        var existing = SegmentStateFile.TryOpen(path);
        if (existing is not null)
        {
            if (existing.TotalLength == probe.TotalLength && existing.Validator == probe.Validator) return existing;
            existing.Dispose();
        }

        return SegmentStateFile.Create(
            path,
            probe.TotalLength!.Value,
            probe.Validator,
            SegmentPlanner.Plan(probe.TotalLength.Value, Math.Max(1, connections)));
    }

    private async Task TransferSegmentsAsync(
        DownloadRequest request,
        ProbeResult probe,
        DownloadTarget target,
        SegmentStateFile state,
        RateLimiter limiter,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var workers = new List<Task>(state.Segments.Length);
        for (var index = 0; index < state.Segments.Length; index++)
        {
            if (state.Segments[index].IsComplete) continue;
            var segment = index;
            workers.Add(TransferSegmentAsync(request, probe, target, state, segment, limiter, reporter, failure.Token));
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            // One failed segment ends the transfer. Letting the others run on would only write
            // bytes that a resumed attempt has to re-validate anyway.
            await failure.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task TransferSegmentAsync(
        DownloadRequest request,
        ProbeResult probe,
        DownloadTarget target,
        SegmentStateFile state,
        int index,
        RateLimiter limiter,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var segment = state.Segments[index];
        using var message = CreateRequest(request, probe);
        message.Headers.Range = new RangeHeaderValue(segment.NextOffset, segment.End);

        // If-Range turns a replaced file into a 200, instead of a silent merge of two versions.
        if (!string.IsNullOrEmpty(probe.Validator) && EntityTagHeaderValue.TryParse(probe.Validator, out var tag))
        {
            message.Headers.IfRange = new RangeConditionHeaderValue(tag);
        }

        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException(
                $"Server stopped honouring ranges (status {(int)response.StatusCode}); the download must restart.");
        }

        await PumpAsync(response, target, segment.NextOffset, segment.Remaining, limiter, reporter,
            written => state.Update(index, segment.Completed + written), cancellationToken).ConfigureAwait(false);
    }

    private async Task TransferWholeAsync(
        DownloadRequest request,
        ProbeResult probe,
        DownloadTarget target,
        RateLimiter limiter,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(request, probe);
        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        await PumpAsync(response, target, 0, long.MaxValue, limiter, reporter, static _ => { }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task PumpAsync(
        HttpResponseMessage response,
        DownloadTarget target,
        long offset,
        long limit,
        RateLimiter limiter,
        ProgressReporter reporter,
        Action<long> checkpoint,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        var written = 0L;
        var sinceCheckpoint = 0L;

        while (written < limit)
        {
            var wanted = (int)Math.Min(buffer.Length, limit - written);

            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(ReadTimeout);

            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, wanted), readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No data received for {ReadTimeout.TotalSeconds:0} seconds.");
            }

            if (read == 0) break;

            await limiter.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
            await target.WriteAsync(buffer.AsMemory(0, read), offset + written, cancellationToken).ConfigureAwait(false);

            written += read;
            sinceCheckpoint += read;
            reporter.Add(read);

            // Checkpointing every megabyte bounds what a crash costs to re-reading that much,
            // without writing the sidecar for every single buffer.
            if (sinceCheckpoint >= CheckpointInterval)
            {
                checkpoint(written);
                sinceCheckpoint = 0;
            }
        }

        checkpoint(written);
    }

    private static HttpRequestMessage CreateRequest(DownloadRequest request, ProbeResult probe)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, probe.FinalUri);
        DownloadHttpClientFactory.PinToHttp11(message);

        if (!string.IsNullOrEmpty(request.Cookies)) message.Headers.TryAddWithoutValidation("Cookie", request.Cookies);
        if (!string.IsNullOrEmpty(request.Referrer)) message.Headers.TryAddWithoutValidation("Referer", request.Referrer);
        if (!string.IsNullOrEmpty(request.UserAgent)) message.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);

        return message;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Aggregates per-connection byte counts into a single throughput figure.</summary>
    private sealed class ProgressReporter(
        IProgress<DownloadProgress>? progress,
        long? total,
        long resumedFrom,
        Func<Segment[]> segments)
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly long _resumedFrom = resumedFrom;
        private long _completed = resumedFrom;
        private long _lastReportTicks;

        public long Completed => Interlocked.Read(ref _completed);

        public void Add(int bytes)
        {
            var completed = Interlocked.Add(ref _completed, bytes);
            if (progress is null) return;

            // Throttle to roughly four updates a second. Eight connections reporting every
            // buffer would flood the UI thread with work it cannot draw.
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastReportTicks);
            if (Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(250)) return;
            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last) return;

            Report(completed);
        }

        public void Final() => Report(Completed);

        private void Report(long completed)
        {
            var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
            var rate = elapsed > 0 ? (completed - _resumedFrom) / elapsed : 0;
            // The snapshot is taken without locking the segment array. A torn read costs one
            // frame of a progress bar; locking would put UI reporting in the transfer path.
            progress?.Report(new DownloadProgress(completed, total, rate, segments()));
        }
    }
}
