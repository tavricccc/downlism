using Microsoft.Data.Sqlite;

namespace Downlism.Core.Downloads;

/// <summary>One remembered transfer, as it is written to and read from the database.</summary>
public sealed record StoredDownload(
    Guid Id,
    string Url,
    string Directory,
    string FileName,
    string? Referrer,
    string State,
    string? Path,
    DateTimeOffset CreatedAt);

/// <summary>
/// The list of downloads, kept across restarts.
/// </summary>
/// <remarks>
/// Only the list lives here. Per-segment progress stays in the sidecar beside each partial
/// file, because it changes tens of times a second and would otherwise bloat the write-ahead
/// log. Cookies are deliberately never stored: they are session credentials, and keeping them
/// on disk to make a resume more convenient is a bad trade. A resumed download that needed a
/// login will fail with a clear message instead.
/// </remarks>
public sealed class DownloadStore
{
    private readonly string _connectionString;

    public DownloadStore(string databasePath)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS downloads (
                id          TEXT PRIMARY KEY,
                url         TEXT NOT NULL,
                directory   TEXT NOT NULL,
                file_name   TEXT NOT NULL,
                referrer    TEXT,
                state       TEXT NOT NULL,
                path        TEXT,
                created_at  TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Downlism",
        "downloads.db");

    public void Save(StoredDownload download)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO downloads (id, url, directory, file_name, referrer, state, path, created_at)
            VALUES ($id, $url, $directory, $fileName, $referrer, $state, $path, $createdAt)
            ON CONFLICT(id) DO UPDATE SET state = excluded.state, path = excluded.path;
            """;

        command.Parameters.AddWithValue("$id", download.Id.ToString("N"));
        command.Parameters.AddWithValue("$url", download.Url);
        command.Parameters.AddWithValue("$directory", download.Directory);
        command.Parameters.AddWithValue("$fileName", download.FileName);
        command.Parameters.AddWithValue("$referrer", (object?)download.Referrer ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", download.State);
        command.Parameters.AddWithValue("$path", (object?)download.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", download.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Delete(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloads WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
    }

    /// <summary>Newest first, matching the order the list is shown in.</summary>
    public IReadOnlyList<StoredDownload> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, url, directory, file_name, referrer, state, path, created_at
            FROM downloads ORDER BY created_at DESC LIMIT 500;
            """;

        var results = new List<StoredDownload>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out var id)) continue;

            results.Add(new StoredDownload(
                id,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTimeOffset.TryParse(reader.GetString(7), out var created) ? created : DateTimeOffset.MinValue));
        }

        return results;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
