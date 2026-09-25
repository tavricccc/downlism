using System.Diagnostics;
using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Downlism.Core.Media;
using Downlism.Core.Torrents;

namespace Downlism.App.Services;

public sealed class DownloadQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly HttpClient _client = DownloadHttpClientFactory.Create(64);
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly MediaTools _tools;
    private readonly TorrentEngine _torrents;
    private readonly ConcurrencyGate _slots;
    private readonly Func<DownloadRequest, ITransferEngine>? _engineFactory;
    private bool _disposed;

    public DownloadQueue(int concurrentDownloads = 3, Func<DownloadRequest, ITransferEngine>? engineFactory = null)
    {
        _slots = new(concurrentDownloads);
        _tools = new(_client);
        _torrents = new(_client);
        _engineFactory = engineFactory;
    }

    public bool MediaToolsReady => _tools.IsReady;
    public Task<MediaFormats> ProbeMediaAsync(DownloadRequest request, Action<string>? status, CancellationToken token) =>
        new MediaProbe(_tools).RunAsync(request, status, token);
    public Task SetTorrentRateLimitAsync(long rate) => _torrents.SetRateLimitAsync(rate);
    private ITransferEngine EngineFor(DownloadRequest request) => _engineFactory?.Invoke(request) ?? (request.Kind switch
    {
        TransferKind.Media => new MediaEngine(_tools),
        TransferKind.Torrent => _torrents,
        _ => new DownloadEngine(_client),
    });
    public RetryPolicy Retry { get; set; } = RetryPolicy.Default;
    public event Action<DownloadJob>? Changed;
    public IEnumerable<DownloadJob> Jobs { get { lock (_sync) return _entries.Values.Select(entry => entry.Job).ToArray(); } }
    public (int Count, double BytesPerSecond) RunningSummary()
    {
        lock (_sync)
        {
            var count = 0;
            var speed = 0d;
            foreach (var entry in _entries.Values)
            {
                if (entry.Job.State != DownloadState.Running) continue;
                count++;
                speed += entry.Job.Progress?.BytesPerSecond ?? 0;
            }
            return (count, speed);
        }
    }

    public DownloadJob Add(DownloadRequest request)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = new Entry(new DownloadJob(Guid.NewGuid(), request));
            _entries.Add(entry.Job.Id, entry);
            Start(entry);
            return entry.Job;
        }
    }

    public DownloadJob Restore(Guid id, DownloadRequest request, DownloadState state, string? path)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var job = new DownloadJob(id, request) { State = state, Path = path,
                Paused = state == DownloadState.Paused, Progress = ReadStoredProgress(request) };
            _entries.Add(id, new Entry(job));
            return job;
        }
    }

    private static DownloadProgress? ReadStoredProgress(DownloadRequest request)
    {
        if (request.FileName is null || request.Kind != TransferKind.Http) return null;
        try
        {
            var name = SuggestedFileName.Sanitize(request.FileName);
            var folder = DownloadCategory.DirectoryFor(request.Directory, name, request.SortIntoCategories, request.CategoryRules);
            var partial = System.IO.Path.Combine(folder, name) + DownloadTarget.PartialExtension;
            if (!File.Exists(partial)) return null;
            using var state = SegmentStateFile.TryOpen(SegmentStateFile.PathFor(partial));
            if (state is null || new FileInfo(partial).Length != state.TotalLength) return null;
            return new(state.CompletedBytes(), state.TotalLength, 0, state.Segments.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public void Pause(Guid id)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry) || !entry.Active) return;
            entry.Job.Paused = true;
            entry.Cancellation!.Cancel();
        }
    }

    public void Resume(Guid id)
    {
        lock (_sync)
        {
            if (_disposed || !_entries.TryGetValue(id, out var entry) || entry.Active ||
                entry.Job.State is not (DownloadState.Paused or DownloadState.Failed)) return;
            Start(entry);
        }
    }

    private void Start(Entry entry)
    {
        entry.Active = true;
        entry.Job.Paused = false;
        entry.Job.Error = null;
        entry.Job.State = DownloadState.Queued;
        entry.Cancellation = new();
        entry.Work = Task.Run(() => RunAsync(entry));
    }

    public void Remove(Guid id)
    {
        lock (_sync)
        {
            if (!_entries.Remove(id, out var entry)) return;
            entry.Job.State = DownloadState.Removed;
            entry.Cancellation?.Cancel();
            Changed?.Invoke(entry.Job);
        }
    }

    public void PauseAll()
    {
        foreach (var job in Jobs.Where(job => job.State is DownloadState.Running or DownloadState.Queued or DownloadState.Retrying)) Pause(job.Id);
    }
    public void ResumeAll()
    {
        foreach (var job in Jobs.Where(job => job.State is DownloadState.Paused or DownloadState.Failed)) Resume(job.Id);
    }
    public void SetConcurrency(int value) => _slots.SetLimit(value);

    private void Publish(Entry entry, Action<DownloadJob>? update = null)
    {
        lock (_sync)
        {
            if (_disposed || entry.Job.State == DownloadState.Removed) return;
            update?.Invoke(entry.Job);
            Changed?.Invoke(entry.Job);
        }
    }

    private async Task RunAsync(Entry entry)
    {
        var job = entry.Job;
        var cancellation = entry.Cancellation!;
        var token = cancellation.Token;
        var finalState = DownloadState.Failed;
        IDisposable? slot = null;
        Timer? heartbeat = null;
        try
        {
            Publish(entry);
            slot = await _slots.EnterAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var progress = new DirectProgress(sample => Publish(entry, job =>
            {
                if (job.Progress is { } previous && (previous.IsAuxiliary != sample.IsAuxiliary
                    || sample.IsAuxiliary && previous.ResolvedFileName != sample.ResolvedFileName))
                    job.Rate.Reset();
                job.Progress = sample with { BytesPerSecond = job.Rate.Observe(sample.CompletedBytes, Stopwatch.GetElapsedTime(0)) };
                if (sample.IsAuxiliary) job.Average.BreakInterval();
                else job.Average.Observe(sample.CompletedBytes, Stopwatch.GetElapsedTime(0));
                if (sample.ResolvedFileName is { } name && sample.ResolvedDirectory is { } folder)
                {
                    job.FileName = name;
                    job.Request = job.Request with { FileName = name, Directory = folder, SortIntoCategories = false };
                }
            }));
            // A stalled connection produces no samples. Keep aging the recent-rate window
            // so the UI and ETA do not display an old nonzero speed until the read timeout.
            heartbeat = new Timer(_ => Publish(entry, current =>
            {
                if (current.State == DownloadState.Running && current.Progress is { } sample)
                    current.Progress = sample with { BytesPerSecond = current.Rate.Current(Stopwatch.GetElapsedTime(0)) };
            }), null, 250, 250);
            for (var attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                Publish(entry, job => { job.Average.BreakInterval(); job.Rate.Reset(); job.State = DownloadState.Running; job.Attempt = attempt; job.Error = null; });
                try
                {
                    var result = await EngineFor(job.Request).RunAsync(job.Request, progress, token).ConfigureAwait(false);
                    Publish(entry, job =>
                    {
                        job.Path = result.Path;
                        job.Progress = new DownloadProgress(result.Bytes, result.Bytes, 0, []);
                        job.FileName = System.IO.Path.GetFileName(result.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                        job.Request = job.Request with { FileName = job.FileName, Directory = System.IO.Path.GetDirectoryName(result.Path) ?? job.Request.Directory, SortIntoCategories = false };
                    });
                    finalState = DownloadState.Completed;
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && Retry.ShouldRetry(ex, attempt))
                {
                    Publish(entry, job => { job.State = DownloadState.Retrying; job.Error = Describe(ex); });
                    await Task.Delay(Retry.DelayBefore(attempt), token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { finalState = DownloadState.Paused; }
        catch (Exception ex) { Publish(entry, job => job.Error = Describe(ex)); }
        finally
        {
            if (heartbeat is not null) await heartbeat.DisposeAsync().ConfigureAwait(false);
            slot?.Dispose();
            lock (_sync)
            {
                entry.Active = false;
                entry.Cancellation = null;
                cancellation.Dispose();
                if (!_disposed && job.State != DownloadState.Removed)
                {
                    job.State = finalState;
                    job.Paused = finalState == DownloadState.Paused;
                    Changed?.Invoke(job);
                }
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        MediaDownloadException or TorrentException or InvalidDataException => ex.Message,
        TimeoutException => "伺服器沒有回應。請檢查連線後繼續下載，或調整逾時秒數。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "檔案已不存在（404）。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } => "伺服器拒絕存取。請重新登入來源網站後取得新的下載連結。",
        HttpRequestException http => http.StatusCode is null ? "連線中斷或回應不完整，可以繼續下載。" : $"伺服器回應 {(int)http.StatusCode}。",
        IOException => "檔案未完成。請確認磁碟空間、資料夾權限，以及是否有另一個下載使用同名檔案。",
        UnauthorizedAccessException => "沒有權限寫入下載資料夾。",
        _ => ex.Message,
    };

    public void Dispose()
    {
        Task[] work;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Cancellation?.Cancel();
            work = _entries.Values.Select(entry => entry.Work).OfType<Task>().ToArray();
            _entries.Clear();
        }
        try { Task.WhenAll(work).Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
        try { _torrents.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
        _client.Dispose();
    }
    private sealed class Entry(DownloadJob job)
    {
        public DownloadJob Job { get; } = job;
        public CancellationTokenSource? Cancellation;
        public Task? Work;
        public bool Active;
    }
    private sealed class DirectProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
}

public enum DownloadState { Queued, Running, Retrying, Paused, Completed, Failed, Removed }

public sealed class DownloadJob(Guid id, DownloadRequest request)
{
    public Guid Id { get; } = id;
    public DownloadRequest Request { get; set; } = request;
    public string FileName { get; set; } = request.FileName ?? SuggestedFileName.FromUri(request.Uri);
    public volatile DownloadState State = DownloadState.Queued;
    public DownloadProgress? Progress { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }
    public bool Paused { get; set; }
    public int Attempt { get; set; } = 1;
    public DownloadAverage Average { get; } = new();
    internal DownloadRate Rate { get; } = new();
    public double? DisplayBytesPerSecond => State switch
    {
        DownloadState.Running => Progress?.BytesPerSecond ?? 0,
        DownloadState.Completed when Average.BytesPerSecond > 0 => Average.BytesPerSecond,
        _ => null,
    };
    public string SpeedLabel => State == DownloadState.Completed ? "平均速度" : "即時速度";
}
