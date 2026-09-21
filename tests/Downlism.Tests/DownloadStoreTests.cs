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
    public void RemembersWhichEngineATransferBelongsTo()
    {
        // Without this a restored magnet link would be restarted by the HTTP engine, which
        // cannot open a magnet at all, and the row would fail the moment it was resumed.
        var torrent = new StoredDownload(
            Guid.NewGuid(),
            "magnet:?xt=urn:btih:0123456789abcdef&dn=debian.iso",
            @"C:\Downloads",
            "debian.iso",
            Referrer: null,
            State: "Paused",
            Path: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Kind: TransferKind.Torrent);

        _store.Save(torrent);

        var loaded = Assert.Single(_store.Load());
        Assert.Equal(TransferKind.Torrent, loaded.Kind);
    }

    [Fact]
    public void RemembersThePageAVideoCameFrom()
    {
        var media = new StoredDownload(
            Guid.NewGuid(),
            "https://cdn.example.com/vod/master.m3u8",
            @"C:\Downloads",
            "講座",
            Referrer: null,
            State: "Paused",
            Path: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Kind: TransferKind.Media,
            PageUrl: "https://example.com/lecture/7");

        _store.Save(media);

        var loaded = Assert.Single(_store.Load());
        Assert.Equal(TransferKind.Media, loaded.Kind);
        Assert.Equal("https://example.com/lecture/7", loaded.PageUrl);
    }

    [Fact]
    public void RemembersMediaOutputAndQuality()
    {
        var media = new StoredDownload(
            Guid.NewGuid(),
            "https://example.com/watch/7",
            @"C:\Downloads",
            "講座",
            Referrer: null,
            State: "Paused",
            Path: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Kind: TransferKind.Media,
            MediaOutput: MediaOutput.Audio,
            MediaQuality: 192);

        _store.Save(media);

        var loaded = Assert.Single(_store.Load());
        Assert.Equal(MediaOutput.Audio, loaded.MediaOutput);
        Assert.Equal(192, loaded.MediaQuality);
    }

    [Fact]
    public void ReadsADatabaseWrittenBeforeTheKindColumnExisted()
    {
        // A person upgrading from 0.3 has a table with eight columns. Recreating it would be
        // simpler and would throw away the list they already have.
        var path = Path.Combine(_directory, "legacy.db");

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE downloads (
                    id          TEXT PRIMARY KEY,
                    url         TEXT NOT NULL,
                    directory   TEXT NOT NULL,
                    file_name   TEXT NOT NULL,
                    referrer    TEXT,
                    state       TEXT NOT NULL,
                    path        TEXT,
                    created_at  TEXT NOT NULL
                );
                INSERT INTO downloads VALUES
                    ('0123456789abcdef0123456789abcdef', 'https://example.com/old.zip',
                     'C:\Downloads', 'old.zip', NULL, 'Completed', NULL, '2026-01-01T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var loaded = Assert.Single(new DownloadStore(path).Load());
        Assert.Equal("old.zip", loaded.FileName);
        Assert.Equal(TransferKind.Http, loaded.Kind);
        Assert.Null(loaded.PageUrl);
        Assert.Equal(MediaOutput.Video, loaded.MediaOutput);
        Assert.Null(loaded.MediaQuality);
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
