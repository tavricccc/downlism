using System.Collections.Concurrent;
using Downlism.Core.Downloads;
using Downlism.Core.Http;

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
    private SemaphoreSlim _slots;

    public DownloadQueue(int concurrentDownloads = 3) => _slots = new SemaphoreSlim(concurrentDownloads, concurrentDownloads);

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

        var directory = DownloadCategory.DirectoryFor(request.Directory, request.FileName, request.SortIntoCategories);
        var partial = Path.Combine(directory, request.FileName) + DownloadTarget.PartialExtension;

        try
        {
            using var state = SegmentStateFile.TryOpen(SegmentStateFile.PathFor(partial));
            if (state is null) return null;

            return new DownloadProgress(state.CompletedBytes(), state.TotalLength, 0, state.Segments.ToArray());
        }
        catch (IOException)
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

    public void SetConcurrency(int concurrentDownloads)
    {
        // Replacing the semaphore only affects transfers that have not started yet; stopping
        // one already in flight to honour a new ceiling would throw away its progress.
        Interlocked.Exchange(ref _slots, new SemaphoreSlim(concurrentDownloads, concurrentDownloads)).Dispose();
    }

    private async Task RunAsync(Entry entry)
    {
        var job = entry.Job;
        var token = entry.Cancellation.Token;

        try
        {
            job.State = DownloadState.Queued;
            Changed?.Invoke(job);

            await _slots.WaitAsync(token).ConfigureAwait(false);
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

                    var result = await new DownloadEngine(_client)
                        .RunAsync(job.Request, progress, token)
                        .ConfigureAwait(false);

                    job.Path = result.Path;
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
                _slots.Release();
            }
            catch (ObjectDisposedException)
            {
                // The concurrency limit was changed while this transfer was running.
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

    public string FileName { get; } = request.FileName ?? SuggestedFileName.FromUri(request.Uri);

    public volatile DownloadState State = DownloadState.Queued;

    public DownloadProgress? Progress { get; set; }

    public string? Path { get; set; }

    public string? Error { get; set; }

    /// <summary>Distinguishes a cancellation the user asked for from one caused by a failure.</summary>
    public bool Paused { get; set; }

    /// <summary>Which attempt is running, counting from one.</summary>
    public int Attempt { get; set; } = 1;
}
