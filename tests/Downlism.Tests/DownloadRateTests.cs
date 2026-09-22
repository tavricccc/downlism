using Downlism.Core.Downloads;
using Downlism.App.Services;
using Xunit;

namespace Downlism.Tests;

public sealed class DownloadRateTests
{
    [Fact]
    public void MeasuresRecentBytesInsteadOfLifetimeAverage()
    {
        var rate = new DownloadRate();
        rate.Observe(0, TimeSpan.Zero);
        rate.Observe(1000, TimeSpan.FromSeconds(1));
        Assert.Equal(1500, rate.Observe(3000, TimeSpan.FromSeconds(2)));
        Assert.Equal(2500, rate.Observe(6000, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void StalledDownloadFallsToZeroWithoutNewSamples()
    {
        var rate = new DownloadRate();
        rate.Observe(0, TimeSpan.Zero);
        rate.Observe(1000, TimeSpan.FromSeconds(1));
        Assert.Equal(500, rate.Current(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, rate.Current(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ResumeAndCounterResetDoNotCountExistingBytes()
    {
        var rate = new DownloadRate();
        Assert.Equal(0, rate.Observe(100000, TimeSpan.Zero));
        Assert.Equal(1000, rate.Observe(101000, TimeSpan.FromSeconds(1)));
        rate.Reset();
        Assert.Equal(0, rate.Observe(101000, TimeSpan.FromHours(1)));
        Assert.Equal(1000, rate.Observe(102000, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)));
        Assert.Equal(0, rate.Observe(0, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(DownloadState.Running, 9000d)]
    [InlineData(DownloadState.Completed, 1000d)]
    [InlineData(DownloadState.Paused, null)]
    [InlineData(DownloadState.Queued, null)]
    [InlineData(DownloadState.Retrying, null)]
    [InlineData(DownloadState.Failed, null)]
    public void ShowsLiveRateOnlyWhileRunningAndAverageOnlyWhenComplete(DownloadState state, double? expected)
    {
        var job = new DownloadJob(Guid.NewGuid(), new DownloadRequest { Uri = new("https://example.test/file"), Directory = Path.GetTempPath() })
        { State = state, Progress = new(5000, 10000, 9000, []) };
        job.Average.Restore(5000, 5);
        Assert.Equal(expected, job.DisplayBytesPerSecond);
        Assert.Equal(state == DownloadState.Completed ? "平均速度" : "即時速度", job.SpeedLabel);
    }
}
