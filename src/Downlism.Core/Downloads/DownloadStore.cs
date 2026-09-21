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
    DateTimeOffset CreatedAt,
    TransferKind Kind = TransferKind.Http,
    string? PageUrl = null,
    MediaOutput MediaOutput = MediaOutput.Video,
    int? MediaQuality = null);

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

        AddColumnIfMissing(connection, "kind");
        AddColumnIfMissing(connection, "page_url");
        AddColumnIfMissing(connection, "media_output");
        AddColumnIfMissing(connection, "media_quality");
    }

    /// <summary>
    /// Widens an existing table in place. Older databases lack newer request options, and
    /// recreating the table would throw away the list a person already has.
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection connection, string column)
    {
        using var existing = connection.CreateCommand();
        existing.CommandText = "SELECT COUNT(*) FROM pragma_table_info('downloads') WHERE name = $name;";
        existing.Parameters.AddWithValue("$name", column);

        if (Convert.ToInt64(existing.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0) return;

        using var add = connection.CreateCommand();
        // The column name comes only from literals in this file, never anything a caller
        // supplies, so interpolating it cannot be turned into an injection.
        add.CommandText = $"ALTER TABLE downloads ADD COLUMN {column} TEXT;";
        add.ExecuteNonQuery();
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
            INSERT INTO downloads
                (id, url, directory, file_name, referrer, state, path, created_at, kind, page_url,
                 media_output, media_quality)
            VALUES
                ($id, $url, $directory, $fileName, $referrer, $state, $path, $createdAt, $kind, $pageUrl,
                 $mediaOutput, $mediaQuality)
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
        command.Parameters.AddWithValue("$kind", download.Kind.ToString());
        command.Parameters.AddWithValue("$pageUrl", (object?)download.PageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$mediaOutput", download.MediaOutput.ToString());
        command.Parameters.AddWithValue("$mediaQuality", (object?)download.MediaQuality ?? DBNull.Value);
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
            SELECT id, url, directory, file_name, referrer, state, path, created_at, kind, page_url,
                   media_output, media_quality
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
                DateTimeOffset.TryParse(reader.GetString(7), out var created) ? created : DateTimeOffset.MinValue,
                !reader.IsDBNull(8) && Enum.TryParse<TransferKind>(reader.GetString(8), out var kind)
                    ? kind
                    : TransferKind.Http,
                reader.IsDBNull(9) ? null : reader.GetString(9),
                !reader.IsDBNull(10) && Enum.TryParse<MediaOutput>(reader.GetString(10), out var output)
                    ? output
                    : MediaOutput.Video,
                reader.IsDBNull(11) ? null : reader.GetInt32(11)));
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
