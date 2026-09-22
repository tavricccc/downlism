using Downlism.Core.Downloads;
using Downlism.Core.Settings;
using Xunit;

namespace Downlism.Tests;

public sealed class CustomizationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("downlism-settings").FullName;
    [Fact]
    public void SettingsRoundTripAndDefaultsAreBackwardCompatible()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = new AppSettings { Connections = 13, ConcurrentDownloads = 7, BytesPerSecond = 123456,
            RetryAttempts = 8, RetryDelaySeconds = 9, ReadTimeoutSeconds = 123, Theme = "Dark",
            CategoryRules = "安裝包=exe,msi\n素材=psd,blend", CloseToTray = false, NotifyOnComplete = true };
        settings.Save(path);
        Assert.Equal(settings, AppSettings.Load(path));
        Assert.Equal(8, AppSettings.FromJson("{}").Connections);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
    [Fact]
    public void CorruptSettingsDoNotPreventStartup()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "not json");
        Assert.Equal(new AppSettings(), AppSettings.Load(path));
        var normalized = new AppSettings { Connections = -1, ConcurrentDownloads = 999, ReadTimeoutSeconds = 0, Theme = "???" }.Normalize();
        normalized.Validate();
        Assert.Equal(1, normalized.Connections);
        Assert.Equal(16, normalized.ConcurrentDownloads);
        Assert.Equal("System", normalized.Theme);
    }
    [Theory]
    [InlineData("../bad=zip")]
    [InlineData("C:\\bad=zip")]
    [InlineData("CON=zip")]
    [InlineData("A=zip\nB=ZIP")]
    [InlineData("A=*")]
    [InlineData("A=")]
    public void RejectsUnsafeOrAmbiguousCategories(string rules) => Assert.Throws<ArgumentException>(() => CategoryRule.Parse(rules));
    [Fact]
    public void CustomRulesOverrideBuiltInsWithoutChangingUnmatchedTypes()
    {
        Assert.Equal(Path.Combine(_directory, "素材"), DownloadCategory.DirectoryFor(_directory, "CLIP.MP4", true, "素材=mp4,blend"));
        Assert.Equal(Path.Combine(_directory, "音樂"), DownloadCategory.DirectoryFor(_directory, "song.mp3", true, "素材=mp4"));
        Assert.Equal(_directory, DownloadCategory.DirectoryFor(_directory, "clip.mp4", false, "素材=mp4"));
    }
    [Fact]
    public async Task GateUpdatesLimitWithoutIssuingAnIndependentPool()
    {
        var gate = new ConcurrencyGate(2);
        using var first = await gate.EnterAsync(default);
        var second = await gate.EnterAsync(default);
        gate.SetLimit(1);
        var third = gate.EnterAsync(default);
        Assert.False(third.IsCompleted);
        second.Dispose();
        Assert.False(third.IsCompleted);
        first.Dispose();
        using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(2));
        var fourth = gate.EnterAsync(default);
        gate.SetLimit(2);
        using var fourthLease = await fourth.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task CancellingWaiterDoesNotLoseSlot()
    {
        var gate = new ConcurrencyGate(1);
        var first = await gate.EnterAsync(default);
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = gate.EnterAsync(default);
        first.Dispose();
        using var lease = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
