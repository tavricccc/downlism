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

    /// <summary>True when the browser is only asking whether Downlism is running.</summary>
    [JsonPropertyName("ping")]
    public bool Ping { get; init; }

    public bool TryGetUri(out Uri uri)
    {
        // http and https only. A file:// or javascript: URL arriving over this channel would
        // turn the extension into a way to make Downlism read local files on a page's behalf.
        if (Uri.TryCreate(Url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
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
