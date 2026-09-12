namespace Downlism.Core.Downloads;

/// <summary>Everything needed to start a transfer, including what the browser knew about it.</summary>
public sealed record DownloadRequest
{
    public required Uri Uri { get; init; }

    public required string Directory { get; init; }

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
    IReadOnlyList<Segment> Segments)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes.Value, 0, 1) : null;

    public TimeSpan? Remaining => TotalBytes is > 0 && BytesPerSecond > 1
        ? TimeSpan.FromSeconds((TotalBytes.Value - CompletedBytes) / BytesPerSecond)
        : null;
}

public sealed record DownloadResult(string Path, long Bytes, TimeSpan Duration);
