using Downlism.Core.Downloads;
using Xunit;

namespace Downlism.Tests;

public sealed class DownloadAverageTests
{
    [Fact]
    public void UsesTransferredBytesOverTimeRatherThanMeanOfSpeeds()
    {
        var average = new DownloadAverage();
        average.Observe(0, TimeSpan.Zero);
        average.Observe(1000, TimeSpan.FromSeconds(1));
        average.Observe(2000, TimeSpan.FromSeconds(10));
        Assert.Equal(200, average.BytesPerSecond);
    }

    [Fact]
    public void ExcludesPauseAndPreviouslyDownloadedBytes()
    {
        var average = new DownloadAverage();
        average.Observe(5000, TimeSpan.Zero);
        average.Observe(6000, TimeSpan.FromSeconds(2));
        average.BreakInterval();
        average.Observe(6000, TimeSpan.FromHours(1));
        average.Observe(9000, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(2));
        Assert.Equal(1000, average.BytesPerSecond);
    }

    [Fact]
    public void IncludesStallsButNotCounterResets()
    {
        var average = new DownloadAverage();
        average.Observe(0, TimeSpan.Zero);
        average.Observe(1000, TimeSpan.FromSeconds(1));
        average.Observe(1000, TimeSpan.FromSeconds(2));
        Assert.Equal(500, average.BytesPerSecond);
        average.Observe(0, TimeSpan.FromSeconds(3));
        average.Observe(1000, TimeSpan.FromSeconds(4));
        Assert.Equal(2000d / 3, average.BytesPerSecond);
    }

    [Fact]
    public void RestoredCountersContinueWithoutCountingDowntime()
    {
        var average = new DownloadAverage();
        average.Restore(2000, 2);
        average.Observe(5000, TimeSpan.FromHours(4));
        average.Observe(7000, TimeSpan.FromHours(4) + TimeSpan.FromSeconds(2));
        Assert.Equal(1000, average.BytesPerSecond);
        Assert.Equal(4000, average.TransferredBytes);
        Assert.Equal(4, average.ActiveSeconds);
    }

    [Fact]
    public void RetainsFinalAverageAndNeverDividesByZero()
    {
        var average = new DownloadAverage();
        average.Observe(0, TimeSpan.Zero);
        Assert.Equal(0, average.BytesPerSecond);
        average.Observe(1000, TimeSpan.FromSeconds(1));
        average.BreakInterval();
        Assert.Equal(1000, average.BytesPerSecond);
    }
}
