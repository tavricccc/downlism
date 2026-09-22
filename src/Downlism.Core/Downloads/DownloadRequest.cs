namespace Downlism.Core.Downloads;

/// <summary>Everything needed to start a transfer, including what the browser knew about it.</summary>
public sealed record DownloadRequest
{
    public required Uri Uri { get; init; }

    /// <summary>Which engine runs this transfer. Defaults to plain segmented HTTP.</summary>
    public TransferKind Kind { get; init; } = TransferKind.Http;

    /// <summary>
    /// The page the media was found on, kept for media transfers. yt-dlp needs a referrer for
    /// manifests that are only served to their own player, and the page is also the only
    /// readable name a bare .m3u8 URL ever has.
    /// </summary>
    public string? PageUrl { get; init; }

    /// <summary>Whether yt-dlp should keep the video or extract an audio file.</summary>
    public MediaOutput MediaOutput { get; init; } = MediaOutput.Video;

    /// <summary>
    /// Maximum video height or MP3 bitrate in kbps. Null asks yt-dlp for its best available
    /// quality without imposing a ceiling.
    /// </summary>
    public int? MediaQuality { get; init; }

    /// <summary>
    /// Where the file goes. When <see cref="SortIntoCategories"/> is set this is the root the
    /// category folder is created under, not the final directory.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// Place the file in a folder chosen from its type. Applied after the response resolves the
    /// real name: a URL ending in .zip that serves a Content-Disposition of .exe belongs with
    /// the programs, and deciding from the URL would file it with the archives.
    /// </summary>
    public bool SortIntoCategories { get; init; }

    /// <summary>Overrides the name derived from the response; null lets the probe decide.</summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Sent verbatim as a <c>Cookie</c> header. Without the session the browser was using,
    /// many servers answer with a login page that would otherwise be saved as the file.
    /// </summary>
    public string? Cookies { get; init; }

    public string? Referrer { get; init; }

    public string? UserAgent { get; init; }

    public int Connections { get; init; } = 8;

    /// <summary>Zero or less means unlimited.</summary>
    public long BytesPerSecond { get; init; }

    public int ReadTimeoutSeconds { get; init; } = 60;
    public string CategoryRules { get; init; } = "";
    public string? ExpectedSha256 { get; init; }
}

/// <summary>
/// The output requested from the media engine.
/// </summary>
public enum MediaOutput
{
    Video,
    Audio,
}

public sealed record DownloadProgress(
    long CompletedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    IReadOnlyList<Segment> Segments,
    string? Note = null,
    string? ResolvedFileName = null,
    string? ResolvedDirectory = null,
    bool IsAuxiliary = false)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes.Value, 0, 1) : null;

    public TimeSpan? Remaining => TotalBytes is > 0 && BytesPerSecond > 1
        ? TimeSpan.FromSeconds(Math.Clamp((TotalBytes.Value - CompletedBytes) / BytesPerSecond, 0, TimeSpan.MaxValue.TotalSeconds - 1))
        : null;
}

public sealed record DownloadResult(string Path, long Bytes, TimeSpan Duration);
