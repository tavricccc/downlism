using System.Net;
using Downlism.Core.Downloads;
using Xunit;

namespace Downlism.Tests;

public sealed class RetryPolicyTests
{
    [Fact]
    public void RetriesADroppedConnection()
    {
        Assert.True(RetryPolicy.Default.ShouldRetry(new IOException("reset by peer"), attempt: 1));
        Assert.True(RetryPolicy.Default.ShouldRetry(new TimeoutException(), attempt: 1));
        Assert.True(RetryPolicy.Default.ShouldRetry(new HttpRequestException("no route"), attempt: 1));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void RetriesServerSideFailures(HttpStatusCode status)
    {
        Assert.True(RetryPolicy.Default.ShouldRetry(new HttpRequestException("", null, status), attempt: 1));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Gone)]
    public void DoesNotRetryASettledAnswer(HttpStatusCode status)
    {
        // Retrying these only delays telling the user the truth.
        Assert.False(RetryPolicy.Default.ShouldRetry(new HttpRequestException("", null, status), attempt: 1));
    }

    [Fact]
    public void DoesNotRetryAnUnrecognisedFailure()
    {
        // An unknown fault is treated as settled so a bug cannot loop forever.
        Assert.False(RetryPolicy.Default.ShouldRetry(new InvalidOperationException(), attempt: 1));
        Assert.False(RetryPolicy.Default.ShouldRetry(new UnauthorizedAccessException(), attempt: 1));
    }

    [Fact]
    public void StopsAtTheAttemptLimit()
    {
        var policy = new RetryPolicy(MaximumAttempts: 3);

        Assert.True(policy.ShouldRetry(new TimeoutException(), attempt: 2));
        Assert.False(policy.ShouldRetry(new TimeoutException(), attempt: 3));
    }

    [Fact]
    public void NoneNeverRetries()
    {
        Assert.False(RetryPolicy.None.ShouldRetry(new TimeoutException(), attempt: 1));
    }

    [Fact]
    public void BackoffGrowsAndThenLevelsOff()
    {
        var policy = new RetryPolicy(MaximumAttempts: 10, BackoffSeconds: 5, MaximumBackoffSeconds: 60);

        Assert.Equal(5, policy.DelayBefore(1).TotalSeconds);
        Assert.Equal(10, policy.DelayBefore(2).TotalSeconds);
        Assert.Equal(20, policy.DelayBefore(3).TotalSeconds);
        Assert.Equal(60, policy.DelayBefore(9).TotalSeconds);
    }
}

public sealed class DownloadCategoryTests
{
    [Theory]
    [InlineData("movie.mkv", "影片")]
    [InlineData("song.FLAC", "音樂")]
    [InlineData("paper.pdf", "文件")]
    [InlineData("archive.tar", "壓縮檔")]
    [InlineData("setup.exe", "程式")]
    [InlineData("photo.HEIC", "圖片")]
    public void SortsByExtension(string fileName, string expected)
    {
        Assert.Equal(expected, DownloadCategory.For(fileName).Name);
    }

    [Theory]
    [InlineData("data.unknownext")]
    [InlineData("noextension")]
    public void LeavesUnrecognisedFilesUncategorised(string fileName)
    {
        Assert.Equal(string.Empty, DownloadCategory.For(fileName).Name);
    }

    [Fact]
    public void BuildsTheTargetDirectory()
    {
        Assert.Equal(
            Path.Combine(@"C:\Downloads", "影片"),
            DownloadCategory.DirectoryFor(@"C:\Downloads", "a.mp4", sortIntoFolders: true));
    }

    [Fact]
    public void UncategorisedFilesStayInTheRoot()
    {
        // Burying an unfamiliar extension somewhere the user would not look is worse than
        // leaving it where they expect it.
        Assert.Equal(@"C:\Downloads", DownloadCategory.DirectoryFor(@"C:\Downloads", "a.xyz", sortIntoFolders: true));
    }

    [Fact]
    public void SortingCanBeTurnedOff()
    {
        Assert.Equal(@"C:\Downloads", DownloadCategory.DirectoryFor(@"C:\Downloads", "a.mp4", sortIntoFolders: false));
    }
}

public sealed class FileHashTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("downlism-hash").FullName;

    [Fact]
    public async Task ComputesAKnownSha256()
    {
        var path = Path.Combine(_directory, "a.txt");
        await File.WriteAllTextAsync(path, "abc");

        var hash = await FileHash.ComputeAsync(path, FileHash.Algorithm.Sha256);

        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", hash);
    }

    [Fact]
    public async Task ReportsProgressWhileHashing()
    {
        var path = Path.Combine(_directory, "big.bin");
        await File.WriteAllBytesAsync(path, new byte[4 * 1024 * 1024]);

        var reports = new List<double>();
        await FileHash.ComputeAsync(path, FileHash.Algorithm.Sha256, new Progress<double>(reports.Add));
        await Task.Delay(100);

        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1], 3);
    }

    [Theory]
    [InlineData("BA7816BF", "ba7816bf")]
    [InlineData("BA7816BF", "BA 78 16 BF")]
    [InlineData("BA7816BF", "ba7816bf  ubuntu.iso")]
    [InlineData("BA7816BF", "  BA7816BF\n")]
    public void AcceptsChecksumsInTheShapePagesPublishThem(string computed, string pasted)
    {
        Assert.True(FileHash.Matches(computed, pasted));
    }

    [Theory]
    [InlineData("BA7816BF", "BA7816BE")]
    [InlineData("BA7816BF", "")]
    [InlineData("BA7816BF", "   ")]
    public void RejectsAnythingElse(string computed, string pasted)
    {
        Assert.False(FileHash.Matches(computed, pasted));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
