using System.Text.Json.Serialization;

namespace Downlism.Core.Ingest;

/// <summary>
/// A download handed over by the browser extension.
/// </summary>
/// <remarks>
/// Every field is attacker-influenced: a page controls the URL it offers and the headers the
/// server returns. Nothing here is trusted as a file system path; the engine sanitises the
/// name and the app decides the directory.
/// </remarks>
public sealed record IngestMessage
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; init; }

    [JsonPropertyName("cookies")]
    public string? Cookies { get; init; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; init; }

    /// <summary>
    /// Which engine the extension believes this belongs to: <c>file</c>, <c>media</c> or
    /// <c>torrent</c>. Only ever narrows what the URL alone would have decided, and anything
    /// unrecognised falls back to that decision.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    /// The page a sniffed stream was found on. A bare manifest URL is often useless on its
    /// own — the page is what yt-dlp can resolve into a title and a format list.
    /// </summary>
    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; init; }

    /// <summary>True when the browser is only asking whether Downlism is running.</summary>
    [JsonPropertyName("ping")]
    public bool Ping { get; init; }

    /// <summary>
    /// http, https and magnet only. A file:// or javascript: URL arriving over this channel
    /// would turn the extension into a way to make Downlism read local files on a page's
    /// behalf. magnet is safe to add for the opposite reason: it can only ever name a swarm,
    /// never a path on this machine.
    /// </summary>
    public bool TryGetUri(out Uri uri) => Downloads.TransferRouting.TryParse(Url, out uri);

    /// <summary>
    /// The engine to use. The extension's opinion is honoured only when the URL could plausibly
    /// support it: a page claiming its link is a torrent must not be able to hand an arbitrary
    /// http address to the BitTorrent session.
    /// </summary>
    public Downloads.TransferKind KindFor(Uri uri)
    {
        var routed = Downloads.TransferRouting.For(uri);

        return Kind switch
        {
            // Media is the one upgrade a page is trusted with, because the extension saw the
            // response headers and the URL alone cannot tell a video stream from a file.
            "media" when uri.Scheme is "http" or "https" => Downloads.TransferKind.Media,
            "file" when routed == Downloads.TransferKind.Media => Downloads.TransferKind.Http,
            _ => routed,
        };
    }
}

public sealed record IngestReply
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    public static IngestReply Ok() => new() { Accepted = true };

    public static IngestReply Rejected(string reason) => new() { Accepted = false, Message = reason };
}

[JsonSerializable(typeof(IngestMessage))]
[JsonSerializable(typeof(IngestReply))]
public sealed partial class IngestJsonContext : JsonSerializerContext;
