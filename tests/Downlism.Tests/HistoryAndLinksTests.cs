using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Xunit;

namespace Downlism.Tests;

public sealed class HistoryAndLinksTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("downlism-history").FullName;
    [Fact]
    public void RestoresAllTransferPreferencesAndResolvedTarget()
    {
        var store = new DownloadStore(Path.Combine(_directory, "history.db"));
        var original = new StoredDownload(Guid.NewGuid(), "https://example.com/download", _directory, "guess.bin", null,
            "Running", null, DateTimeOffset.UtcNow, Connections: 13, BytesPerSecond: 123456, ReadTimeoutSeconds: 99,
            SortIntoCategories: true, CategoryRules: "素材=bin", ExpectedSha256: new string('a', 64));
        store.Save(original);
        var updated = original with { Directory = Path.Combine(_directory, "素材"), FileName = "real.bin",
            SortIntoCategories = false, State = "Paused", CreatedAt = original.CreatedAt.AddHours(1) };
        store.Save(updated);
        var loaded = Assert.Single(store.Load());
        Assert.Equal(updated with { CreatedAt = original.CreatedAt }, loaded);
    }
    [Fact]
    public void LinkListsDeduplicatePreserveOrderAndIgnoreCommentLines()
    {
        var list = LinkList.Parse("# Export\nhttps://example.com/a\n\nhttps://example.com/a\nhttps://example.com/b");
        Assert.Equal(2, list.Links.Count);
        Assert.Equal(1, list.Duplicates);
        Assert.EndsWith("/a", list.Links[0].AbsoluteUri, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void AcceptsWinUiAndTextFileLineEndings(string separator)
    {
        Assert.Equal(2, LinkList.Parse("https://example.com/a" + separator + "https://example.com/b").Links.Count);
        Assert.Equal(2, CategoryRule.Parse("一=bin" + separator + "二=zip").Count);
    }
    [Theory]
    [InlineData("https://example.com/a https://example.com/b")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("https://user:password@example.com/a")]
    [InlineData("# only a comment")]
    public void RejectsUnsafeOrMalformedBatchLines(string text) => Assert.Throws<ArgumentException>(() => LinkList.Parse(text));
    [Fact]
    public void LimitsBatchSize() => Assert.Throws<ArgumentException>(() => LinkList.Parse("https://example.com/a\nhttps://example.com/b", 1));
    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("LPT1.exe", "_LPT1.exe")]
    [InlineData("COM¹", "_COM¹")]
    [InlineData("invoice\u202Efdp.exe", "invoice_fdp.exe")]
    public void NeutralizesDeviceNamesAndBidiSpoofing(string input, string expected) => Assert.Equal(expected, SuggestedFileName.Sanitize(input));
    [Fact]
    public void LongNamesKeepExtensionAndSpaceForCheckpointSuffix()
    {
        var name = SuggestedFileName.Sanitize(new string('a', 300) + ".zip");
        Assert.True(name.Length <= 220);
        Assert.EndsWith(".zip", name, StringComparison.Ordinal);
        Assert.True((name + ".download.dlstate").Length < 255);
    }
    public void Dispose()
    {
        // Disposing a connection returns it to SQLite's pool; it does not release the file.
        // Clear this test database's pool before deleting its temporary directory.
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "history.db"),
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        Directory.Delete(_directory, true);
    }
}
