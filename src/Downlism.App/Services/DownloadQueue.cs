using System.Collections.Concurrent;
using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Downlism.Core.Media;
using Downlism.Core.Torrents;

namespace Downlism.App.Services;

/// <summary>
/// Owns every running transfer and the limit on how many run at once.
/// </summary>
/// <remarks>
/// The queue exists because unlimited parallelism is slower, not faster: twenty transfers of
/// eight connections each is a hundred and sixty sockets competing for one uplink, and every
/// one of them finishes late. Holding the rest back means the first few finish early.
/// </remarks>
public sealed class DownloadQueue : IDisposable
{
    private readonly HttpClient _client = DownloadHttpClientFactory.Create(64);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    // Both of these hold state that must not be per-transfer: the tools are two executables on
    // disk that several jobs would otherwise race to install, and the BitTorrent session is one
    // listening port and one DHT table shared by every torrent.
    private readonly MediaTools _tools;
    private readonly TorrentEngine _torrents;

    private SemaphoreSlim _slots;

    public DownloadQueue(int concurrentDownloads = 3)
    {
        _slots = new SemaphoreSlim(concurrentDownloads, concurrentDownloads);
        _tools = new MediaTools(_client);
        _torrents = new TorrentEngine(_client);
    }

    /// <summary>
    /// Whether the media tools have already been fetched. The window uses it to warn once,
    /// before the first video download spends several minutes looking like it has stalled.
    /// </summary>
    public bool MediaToolsReady => _tools.IsReady;

    /// <summary>
    /// The BitTorrent session throttles in one place for every torrent at once, unlike HTTP
    /// where each transfer carries its own ceiling.
    /// </summary>
    public Task SetTorrentRateLimitAsync(long bytesPerSecond) => _torrents.SetRateLimitAsync(bytesPerSecond);

    /// <summary>
    /// Picks the engine for a request. This is the only place that knows there is more than
    /// one; everything else in the queue treats every transfer identically.
    /// </summary>
    private ITransferEngine EngineFor(DownloadRequest request) => request.Kind switch
    {
        TransferKind.Media => new MediaEngine(_tools),
        TransferKind.Torrent => _torrents,
        _ => new DownloadEngine(_client),
    };

    /// <summary>How a transfer that fails for a transient reason is retried.</summary>
    public RetryPolicy Retry { get; set; } = RetryPolicy.Default;

    /// <summary>Raised on a background thread whenever a transfer changes state.</summary>
    public event Action<DownloadJob>? Changed;

    public IEnumerable<DownloadJob> Jobs => _entries.Values.Select(entry => entry.Job);

    public DownloadJob Add(DownloadRequest request)
    {
        var job = new DownloadJob(Guid.NewGuid(), request);
        var entry = new Entry(job, new CancellationTokenSource());
        _entries[job.Id] = entry;

        _ = RunAsync(entry);
        return job;
    }

    /// <summary>
    /// Re-enters a transfer from a previous session without starting it. The partial file and
    /// its sidecar are still on disk, so Resume picks up where the last run stopped.
    /// </summary>
    public DownloadJob Restore(Guid id, DownloadRequest request, DownloadState state, string? path)
    {
        var job = new DownloadJob(id, request)
        {
            State = state,
            Path = path,
            Paused = state == DownloadState.Paused,
            Progress = ReadStoredProgress(request),
        };

        _entries[id] = new Entry(job, new CancellationTokenSource());
        return job;
    }

    /// <summary>
    /// Recovers what the sidecar knows, so a restored row shows where each connection stopped
    /// instead of an empty bar that implies the previous session achieved nothing.
    /// </summary>
    private static DownloadProgress? ReadStoredProgress(DownloadRequest request)
    {
        if (request.FileName is null) return null;

        // Sanitised for the same reason the engine sanitises it: this name reaches us from a
        // web page by way of the extension, and an unsanitised one turns Path.Combine into an
        // exception at best and a path outside the download folder at worst.
        var fileName = SuggestedFileName.Sanitize(request.FileName);
        var directory = DownloadCategory.DirectoryFor(request.Directory, fileName, request.SortIntoCategories);
        var partial = Path.Combine(directory, fileName) + DownloadTarget.PartialExtension;

        try
        {
            using var state = SegmentStateFile.TryOpen(SegmentStateFile.PathFor(partial));
            if (state is null) return null;

            return new DownloadProgress(state.CompletedBytes(), state.TotalLength, 0, state.Segments.ToArray());
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stops a transfer, keeping its partial file and sidecar so it can be resumed. Pausing is
    /// the same operation as failing here; the difference is only what the user is told.
    /// </summary>
    public void Pause(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;

        entry.Job.Paused = true;
        entry.Cancellation.Cancel();
    }

    public void Resume(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        if (entry.Job.State is DownloadState.Running or DownloadState.Completed) return;

        var replacement = new Entry(entry.Job, new CancellationTokenSource());
        entry.Job.Paused = false;
        _entries[id] = replacement;

        _ = RunAsync(replacement);
    }

    /// <summary>Cancels a transfer and removes it from the list, leaving the partial file.</summary>
    public void Remove(Guid id)
    {
        if (!_entries.TryRemove(id, out var entry)) return;

        entry.Cancellation.Cancel();
        entry.Job.State = DownloadState.Removed;
        Changed?.Invoke(entry.Job);
    }

    public void PauseAll()
    {
        foreach (var entry in _entries.Values.Where(entry => entry.Job.State == DownloadState.Running))
        {
            Pause(entry.Job.Id);
        }
    }

    /// <summary>
    /// Changes how many transfers may run at once.
    /// </summary>
    /// <remarks>
    /// The new ceiling applies to transfers that have not started yet. Stopping one already in
    /// flight to honour it would throw away its progress, and the person who lowered the limit
    /// wanted less contention, not less finished work.
    ///
    /// The replaced semaphore is deliberately not disposed. Transfers already holding one of
    /// its slots release it when they end, and a transfer still queued is waiting on it; either
    /// one touching a disposed semaphore throws from a path that has no business failing. It is
    /// an ordinary object and is collected once the last of them has let go.
    /// </remarks>
    public void SetConcurrency(int concurrentDownloads) =>
        Interlocked.Exchange(ref _slots, new SemaphoreSlim(concurrentDownloads, concurrentDownloads));

    private async Task RunAsync(Entry entry)
    {
        var job = entry.Job;
        var token = entry.Cancellation.Token;

        // Captured, not read again later: the limit can be changed while this transfer runs,
        // and releasing a slot back into a semaphore that never issued it throws.
        var slots = _slots;

        try
        {
            job.State = DownloadState.Queued;
            Changed?.Invoke(job);

            await slots.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Settle(job, paused: job.Paused);
            return;
        }

        try
        {
            var progress = new Progress<DownloadProgress>(sample =>
            {
                job.Progress = sample;
                Changed?.Invoke(job);
            });

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    job.State = DownloadState.Running;
                    job.Attempt = attempt;
                    job.Error = null;
                    Changed?.Invoke(job);

                    var result = await EngineFor(job.Request)
                        .RunAsync(job.Request, progress, token)
                        .ConfigureAwait(false);

                    job.Path = result.Path;
                    // The engines that choose their own name only reveal it at the end.
                    if (job.Request.Kind != TransferKind.Http)
                    {
                        job.FileName = Path.GetFileName(result.Path.TrimEnd(Path.DirectorySeparatorChar));
                    }

                    job.State = DownloadState.Completed;
                    Changed?.Invoke(job);
                    return;
                }
                catch (Exception exception) when (Retry.ShouldRetry(exception, attempt) && !token.IsCancellationRequested)
                {
                    // The partial file and its sidecar are still on disk, so the next attempt
                    // resumes rather than starting the transfer again.
                    job.Error = Describe(exception);
                    job.State = DownloadState.Retrying;
                    Changed?.Invoke(job);

                    await Task.Delay(Retry.DelayBefore(attempt), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Settle(job, paused: job.Paused);
        }
        catch (Exception exception)
        {
            job.Error = Describe(exception);
            Settle(job, paused: false);
        }
        finally
        {
            try
            {
                slots.Release();
            }
            catch (ObjectDisposedException)
            {
                // The queue was torn down while this transfer was running.
            }

            entry.Cancellation.Dispose();
        }
    }

    private void Settle(DownloadJob job, bool paused)
    {
        job.State = paused ? DownloadState.Paused : DownloadState.Failed;
        Changed?.Invoke(job);
    }

    /// <summary>Turns an exception into something a person can act on.</summary>
    private static string Describe(Exception exception) => exception switch
    {
        // These two already speak for themselves: yt-dlp and MonoTorrent report failures the
        // person can act on, and restating them as "下載失敗" would throw that away.
        MediaDownloadException media => media.Message,
        TorrentException torrent => torrent.Message,
        TimeoutException => "伺服器沒有回應，已停止。可以繼續下載。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "檔案已不存在（404）。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "伺服器拒絕存取（403）。可能需要重新登入來源網站。",
        HttpRequestException http => http.StatusCode is null
            ? "無法連線到伺服器。"
            : $"伺服器回應 {(int)http.StatusCode}。",
        IOException => "寫入檔案失敗，請確認磁碟空間與資料夾權限。",
        UnauthorizedAccessException => "沒有權限寫入下載資料夾。",
        _ => exception.Message,
    };

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Cancellation.Cancel();
            entry.Cancellation.Dispose();
        }

        _entries.Clear();
        _slots.Dispose();

        // Waited on, briefly. A torrent session that is not stopped leaves sockets open and its
        // fast resume unwritten, which costs a full rehash on the next run; waiting forever on
        // tracker goodbyes during application exit costs more than that is worth.
        _torrents.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4));
        _client.Dispose();
    }

    private sealed record Entry(DownloadJob Job, CancellationTokenSource Cancellation);
}

public enum DownloadState
{
    Queued,
    Running,
    Retrying,
    Paused,
    Completed,
    Failed,
    Removed,
}

/// <summary>One transfer, as the app tracks it.</summary>
public sealed class DownloadJob(Guid id, DownloadRequest request)
{
    public Guid Id { get; } = id;

    public DownloadRequest Request { get; } = request;

    /// <summary>
    /// Not fixed at creation. A video is named by yt-dlp and a torrent by its own metadata, so
    /// the label a row starts with is a placeholder that the finished transfer replaces.
    /// </summary>
    public string FileName { get; set; } = request.FileName ?? SuggestedFileName.FromUri(request.Uri);

    public volatile DownloadState State = DownloadState.Queued;

    public DownloadProgress? Progress { get; set; }

    public string? Path { get; set; }

    public string? Error { get; set; }

    /// <summary>Distinguishes a cancellation the user asked for from one caused by a failure.</summary>
    public bool Paused { get; set; }

    /// <summary>Which attempt is running, counting from one.</summary>
    public int Attempt { get; set; } = 1;
}
