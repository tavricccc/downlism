namespace Downlism.Core.Downloads;

/// <summary>
/// One way of turning a request into a file on disk.
/// </summary>
/// <remarks>
/// Three of these exist and they have almost nothing in common underneath: segmented HTTP
/// writes byte ranges itself, media shells out to yt-dlp, BitTorrent runs a swarm. What they
/// do share is everything above them — the queue, the concurrency limit, the retry policy,
/// the row, the resume button, the notification. The interface is deliberately narrow so that
/// shared part never has to know which engine it is driving.
/// </remarks>
public interface ITransferEngine
{
    Task<DownloadResult> RunAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Which engine a request belongs to.</summary>
public enum TransferKind
{
    /// <summary>A plain file over HTTP, segmented when the server allows it.</summary>
    Http,

    /// <summary>A page or manifest that yt-dlp resolves into a video.</summary>
    Media,

    /// <summary>A magnet link or .torrent file.</summary>
    Torrent,
}
