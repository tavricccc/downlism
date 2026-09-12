using Downlism.Core.Downloads;
using Xunit;

namespace Downlism.Tests;

public sealed class DownloadStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("downlism-store").FullName;
    private readonly DownloadStore _store;

    public DownloadStoreTests() => _store = new DownloadStore(Path.Combine(_directory, "downloads.db"));

    private static StoredDownload Sample(string name, string state = "Running") => new(
        Guid.NewGuid(),
        $"https://example.com/{name}",
        @"C:\Downloads",
        name,
        Referrer: "https://example.com/",
        State: state,
        Path: null,
        CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void RemembersADownloadAcrossReads()
    {
        var download = Sample("a.zip");
        _store.Save(download);

        var loaded = Assert.Single(_store.Load());
        Assert.Equal(download.Id, loaded.Id);
        Assert.Equal(download.Url, loaded.Url);
        Assert.Equal("a.zip", loaded.FileName);
        Assert.Equal("https://example.com/", loaded.Referrer);
    }

    [Fact]
    public void UpdatingStateDoesNotCreateASecondRow()
    {
        var download = Sample("b.zip");
        _store.Save(download);
        _store.Save(download with { State = "Completed", Path = @"C:\Downloads\b.zip" });

        var loaded = Assert.Single(_store.Load());
        Assert.Equal("Completed", loaded.State);
        Assert.Equal(@"C:\Downloads\b.zip", loaded.Path);
    }

    [Fact]
    public void ReturnsNewestFirst()
    {
        var older = Sample("older.zip") with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var newer = Sample("newer.zip");

        _store.Save(older);
        _store.Save(newer);

        Assert.Equal(["newer.zip", "older.zip"], _store.Load().Select(row => row.FileName));
    }

    [Fact]
    public void DeleteRemovesTheRow()
    {
        var download = Sample("c.zip");
        _store.Save(download);
        _store.Delete(download.Id);

        Assert.Empty(_store.Load());
    }

    [Fact]
    public void SurvivesReopeningTheDatabase()
    {
        var path = Path.Combine(_directory, "reopen.db");
        new DownloadStore(path).Save(Sample("d.zip"));

        Assert.Single(new DownloadStore(path).Load());
    }

    public void Dispose()
    {
        // The connection is closed per operation, so the file is free to delete here.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
