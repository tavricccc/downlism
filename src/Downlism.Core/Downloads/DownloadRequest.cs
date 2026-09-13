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
}

/// <summary>
/// A progress sample. <see cref="Segments"/> carries the live per-connection state so the UI
/// can show where each connection has reached, rather than one averaged bar.
/// </summary>
public sealed record DownloadProgress(
    long CompletedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    IReadOnlyList<Segment> Segments,
    string? Note = null)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes.Value, 0, 1) : null;

    public TimeSpan? Remaining => TotalBytes is > 0 && BytesPerSecond > 1
        ? TimeSpan.FromSeconds((TotalBytes.Value - CompletedBytes) / BytesPerSecond)
        : null;
}

public sealed record DownloadResult(string Path, long Bytes, TimeSpan Duration);
