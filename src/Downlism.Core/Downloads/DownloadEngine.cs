using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Downlism.Core.Http;

namespace Downlism.Core.Downloads;

public sealed class DownloadEngine(HttpClient client) : ITransferEngine
{
    private const int BufferSize = 64 * 1024;
    private const long CheckpointInterval = 1024 * 1024;

    public async Task<DownloadResult> RunAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var probe = await new DownloadProbe(client).ProbeAsync(request, cancellationToken).ConfigureAwait(false);
        if (probe.IsEncoded) throw new InvalidDataException("伺服器忽略 identity 編碼要求，無法安全儲存這個檔案。");
        if (!string.IsNullOrEmpty(request.ExpectedSha256) &&
            (request.ExpectedSha256.Length != 64 || !request.ExpectedSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("SHA-256 必須是 64 位十六進位字元。");

        var segmented = probe.SupportsRanges && probe.TotalLength is > 0;
        var fileName = SuggestedFileName.Sanitize(request.FileName ?? probe.FileName);
        var directory = DownloadCategory.DirectoryFor(request.Directory, fileName, request.SortIntoCategories, request.CategoryRules);
        var partialPath = Path.Combine(directory, fileName) + DownloadTarget.PartialExtension;
        var canResume = File.Exists(partialPath) && new FileInfo(partialPath).Length == probe.TotalLength;
        using var target = DownloadTarget.Open(directory, fileName, probe.TotalLength, resume: segmented);
        var statePath = SegmentStateFile.PathFor(target.PartialPath);
        // A filename and an ETag alone are not resource identities: many unrelated servers use
        // the same tag. Old unbound sidecars safely restart rather than mix unrelated bytes.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Uri.AbsoluteUri + "\n" + probe.Validator)));
        SegmentStateFile? state = null;
        try
        {
            state = segmented ? OpenOrCreateState(statePath, probe, request.Connections, canResume, identity) : null;
            var snapshot = state;
            var reporter = new ProgressReporter(progress, probe.TotalLength, state?.CompletedBytes() ?? 0,
                () => snapshot?.Segments.ToArray() ?? [], fileName, directory);
            reporter.Final();
            var limiter = new RateLimiter(request.BytesPerSecond);
            if (state is null)
                await TransferWholeAsync(request, probe, target, limiter, reporter, cancellationToken).ConfigureAwait(false);
            else
                await TransferSegmentsAsync(request, probe, target, state, limiter, reporter, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (state is not null && !state.IsComplete()) throw new EndOfStreamException("下載分段尚未完成。");
            target.Flush();
            state?.Flush();
            state?.Dispose();
            state = null;
            if (!string.IsNullOrEmpty(request.ExpectedSha256))
            {
                target.Dispose();
                var actual = await FileHash.ComputeAsync(target.PartialPath, FileHash.Algorithm.Sha256,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!FileHash.Matches(actual, request.ExpectedSha256))
                {
                    TryDelete(statePath);
                    throw new InvalidDataException("SHA-256 不符。檔案保留為 .download，未標示完成；請確認來源與校驗碼後再試。");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = target.Publish(probe.FinalUri);
            TryDelete(statePath);
            reporter.Final();
            return new(path, reporter.Completed, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally { state?.Dispose(); }
    }

    private static SegmentStateFile OpenOrCreateState(string path, ProbeResult probe, int connections, bool canResume, string identity)
    {
        var existing = SegmentStateFile.TryOpen(path);
        if (existing is not null)
        {
            if (canResume && existing.TotalLength == probe.TotalLength && existing.Validator == identity) return existing;
            existing.Dispose();
        }
        return SegmentStateFile.Create(path, probe.TotalLength!.Value, identity,
            SegmentPlanner.Plan(probe.TotalLength.Value, Math.Clamp(connections, 1, 32)));
    }

    private async Task TransferSegmentsAsync(DownloadRequest request, ProbeResult probe, DownloadTarget target,
        SegmentStateFile state, RateLimiter limiter, ProgressReporter reporter, CancellationToken token)
    {
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? firstFailure = null;
        var workers = new List<Task>(state.Segments.Length);
        for (var index = 0; index < state.Segments.Length; index++)
            if (!state.Segments[index].IsComplete) workers.Add(WorkerAsync(index));
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch
        {
            if (firstFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
            throw;
        }

        async Task WorkerAsync(int index)
        {
            try { await TransferSegmentAsync(request, probe, target, state, index, limiter, reporter, failure.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref firstFailure, ex, null);
                // Cancel at the first failure, not after WhenAll has waited for every sibling.
                await failure.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task TransferSegmentAsync(DownloadRequest request, ProbeResult probe, DownloadTarget target,
        SegmentStateFile state, int index, RateLimiter limiter, ProgressReporter reporter, CancellationToken token)
    {
        var segment = state.Segments[index];
        using var message = CreateRequest(request, probe);
        message.Headers.Range = new RangeHeaderValue(segment.NextOffset, segment.End);
        if (EntityTagHeaderValue.TryParse(probe.Validator, out var tag) && !tag.IsWeak)
            message.Headers.IfRange = new RangeConditionHeaderValue(tag);
        else if (DateTimeOffset.TryParse(probe.Validator, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            message.Headers.IfRange = new RangeConditionHeaderValue(date);
        using var response = await DownloadHeaders.SendAsync(client, message, request.ReadTimeoutSeconds, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            !ContentRange.TryParse(response.Content.Headers.ContentRange?.ToString(), out var range) ||
            range.Start != segment.NextOffset || range.End != segment.End || range.TotalLength != probe.TotalLength ||
            response.Content.Headers.ContentEncoding.Count > 0 ||
            (response.Content.Headers.ContentLength is { } length && length != segment.Remaining))
            throw new InvalidDataException("伺服器回傳的下載範圍不符，已停止以避免產生損壞檔案。");
        if (response.Headers.ETag is { IsWeak: false } actualTag &&
            EntityTagHeaderValue.TryParse(probe.Validator, out var expectedTag) && actualTag.ToString() != expectedTag.ToString())
            throw new InvalidDataException("來源檔案在下載期間已變更，請重新下載。");
        await PumpAsync(response, target, segment.NextOffset, segment.Remaining, request.ReadTimeoutSeconds, limiter, reporter,
            written => state.Update(index, segment.Completed + written), token).ConfigureAwait(false);
    }

    private async Task TransferWholeAsync(DownloadRequest request, ProbeResult probe, DownloadTarget target,
        RateLimiter limiter, ProgressReporter reporter, CancellationToken token)
    {
        // The empty-file probe may answer 416; there is no body to download.
        if (probe.TotalLength == 0) return;
        using var message = CreateRequest(request, probe);
        using var response = await DownloadHeaders.SendAsync(client, message, request.ReadTimeoutSeconds, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count > 0)
            throw new InvalidDataException("伺服器未回傳完整、未壓縮的檔案。");
        if (probe.TotalLength is { } expected && response.Content.Headers.ContentLength is { } actual && actual != expected)
            throw new InvalidDataException("來源檔案大小在探測後已變更，請重新下載。");
        await PumpAsync(response, target, 0, probe.TotalLength ?? response.Content.Headers.ContentLength,
            request.ReadTimeoutSeconds, limiter, reporter, static _ => { }, token).ConfigureAwait(false);
    }

    private static async Task PumpAsync(HttpResponseMessage response, DownloadTarget target, long offset, long? expected,
        int timeoutSeconds, RateLimiter limiter, ProgressReporter reporter, Action<long> checkpoint, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[BufferSize];
        var written = 0L;
        var sinceCheckpoint = 0L;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            while (true)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600)));
                int read;
                try { read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("伺服器超過設定時間未傳送資料。"); }
                finally { deadline.CancelAfter(Timeout.InfiniteTimeSpan); }
                if (deadline.IsCancellationRequested && !token.IsCancellationRequested) throw new TimeoutException("讀取逾時。");
                if (read == 0) break;
                if (expected is { } limit && read > limit - written)
                    throw new InvalidDataException("回應資料超出要求的下載範圍。");
                await limiter.ConsumeAsync(read, token).ConfigureAwait(false);
                await target.WriteAsync(buffer.AsMemory(0, read), offset + written, token).ConfigureAwait(false);
                written += read;
                sinceCheckpoint += read;
                reporter.Add(read);
                if (sinceCheckpoint >= CheckpointInterval) { checkpoint(written); sinceCheckpoint = 0; }
            }
            if (expected is { } size && written != size) throw new EndOfStreamException("伺服器提早中斷傳輸，檔案尚未完整。");
        }
        finally { checkpoint(written); }
    }

    private static HttpRequestMessage CreateRequest(DownloadRequest request, ProbeResult probe)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, probe.FinalUri);
        DownloadHeaders.Apply(message, request);
        return message;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ProgressReporter(IProgress<DownloadProgress>? progress, long? total, long resumedFrom,
        Func<Segment[]> segments, string fileName, string directory)
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
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastReportTicks);
            if (Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(250)) return;
            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) == last) Report(completed);
        }
        public void Final() => Report(Completed);
        private void Report(long completed)
        {
            var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
            progress?.Report(new(completed, total, elapsed > 0 ? (completed - _resumedFrom) / elapsed : 0,
                segments(), ResolvedFileName: fileName, ResolvedDirectory: directory));
        }
    }
}
