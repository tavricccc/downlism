using Downlism.App.Services;
using Downlism.Core.Downloads;
using Xunit;

namespace Downlism.Tests;

public sealed class QueueTests
{
    private static DownloadRequest Request => new() { Uri = new("https://example.com/file"), Directory = Path.GetTempPath() };
    [Fact]
    public async Task ResumeQueuedJobCannotStartDuplicateAndPauseAllIncludesWaitingJobs()
    {
        var engine = new WaitingEngine();
        using var queue = new DownloadQueue(1, _ => engine);
        var first = queue.Add(Request);
        await Until(() => first.State == DownloadState.Running);
        var second = queue.Add(Request);
        queue.Resume(second.Id);
        queue.Resume(second.Id);
        queue.PauseAll();
        await Until(() => first.State == DownloadState.Paused && second.State == DownloadState.Paused);
        Assert.Equal(1, engine.Calls);
        queue.Resume(second.Id);
        await Until(() => second.State == DownloadState.Running);
        Assert.Equal(2, engine.Calls);
        queue.Pause(second.Id);
        await Until(() => second.State == DownloadState.Paused);
    }
    [Fact]
    public async Task RemovedRunningJobIsNeverResurrectedByCancellation()
    {
        using var queue = new DownloadQueue(1, _ => new WaitingEngine());
        var job = queue.Add(Request);
        await Until(() => job.State == DownloadState.Running);
        queue.Remove(job.Id);
        await Task.Delay(100);
        Assert.Equal(DownloadState.Removed, job.State);
        Assert.Empty(queue.Jobs);
        queue.Dispose();
    }
    [Fact]
    public async Task CompletedJobsCanBeRemovedAndQueueCanBeDisposedWithoutDisposedTokenErrors()
    {
        using var queue = new DownloadQueue(1, _ => new CompletedEngine());
        var job = queue.Add(Request);
        await Until(() => job.State == DownloadState.Completed);
        queue.Pause(job.Id);
        queue.Resume(job.Id);
        queue.Remove(job.Id);
        Assert.Empty(queue.Jobs);
    }
    [Fact]
    public async Task AverageExcludesToolsAndSurvivesCompletion()
    {
        using var queue = new DownloadQueue(1, _ => new ReportingEngine());
        var job = queue.Add(Request);
        await Until(() => job.State == DownloadState.Completed);
        Assert.Equal(1000, job.Average.TransferredBytes);
        Assert.True(job.Average.BytesPerSecond > 0);
        Assert.Equal(2000, job.Progress!.CompletedBytes);
        Assert.Equal(1, job.Progress.Fraction);
    }

    private sealed class ReportingEngine : ITransferEngine
    {
        public async Task<DownloadResult> RunAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken token)
        {
            progress!.Report(new(0, 100000, 0, [], IsAuxiliary: true));
            await Task.Delay(10, token);
            progress.Report(new(100000, 100000, 1000, [], IsAuxiliary: true));
            progress.Report(new(1000, 2000, 0, [])); // Already resumed bytes.
            await Task.Delay(10, token);
            progress.Report(new(2000, 2000, 1000, []));
            return new(Path.Combine(request.Directory, "test.bin"), 2000, TimeSpan.FromMilliseconds(20));
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class WaitingEngine : ITransferEngine
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public async Task<DownloadResult> RunAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
    private sealed class CompletedEngine : ITransferEngine
    {
        public Task<DownloadResult> RunAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadResult(Path.Combine(request.Directory, "test.bin"), 10, TimeSpan.Zero));
    }
}
