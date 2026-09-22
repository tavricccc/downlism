using System.Net;
using Downlism.Core.Downloads;

namespace Downlism.Core.Http;

/// <summary>What a probe learned about a URL before any bytes are committed to disk.</summary>
public sealed record ProbeResult
{
    public required Uri FinalUri { get; init; }

    /// <summary>Null when the server would not commit to a length.</summary>
    public required long? TotalLength { get; init; }

    /// <summary>Whether the server answered a byte range, which is what resume depends on.</summary>
    public required bool SupportsRanges { get; init; }

    /// <summary>The <c>ETag</c> or <c>Last-Modified</c> used later in <c>If-Range</c>.</summary>
    public required string Validator { get; init; }

    public required string FileName { get; init; }

    /// <summary>True when the body is content-encoded and therefore cannot be segmented.</summary>
    public required bool IsEncoded { get; init; }
}

/// <summary>Inspects a URL before download.</summary>
public sealed class DownloadProbe(HttpClient client)
{
    private const int MaximumRedirects = 10;

    /// <summary>
    /// Probes with <c>Range: bytes=0-0</c> rather than <c>HEAD</c>. CDNs reject or misreport
    /// HEAD often enough to be unreliable, and a one-byte GET settles the length, resume
    /// support, final URL, validator and file name in a single round trip.
    /// </summary>
    public Task<ProbeResult> ProbeAsync(Uri uri, CancellationToken cancellationToken) =>
        ProbeAsync(new DownloadRequest { Uri = uri, Directory = "" }, cancellationToken);

    public async Task<ProbeResult> ProbeAsync(DownloadRequest source, CancellationToken cancellationToken)
    {
        var current = source.Uri;

        for (var hop = 0; hop <= MaximumRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            DownloadHeaders.Apply(request, source);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

            using var response = await DownloadHeaders.SendAsync(client, request, source.ReadTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme is not ("http" or "https") || (current.Scheme == "https" && next.Scheme == "http"))
                    throw new HttpRequestException("拒絕不安全的重新導向。");
                current = next;
                continue;
            }

            // An empty file has no satisfiable byte 0. RFC 9110 allows bytes */0 here.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                response.Content.Headers.ContentRange is { HasLength: true, Length: 0 })
                return Describe(current, response) with { TotalLength = 0, SupportsRanges = false };
            response.EnsureSuccessStatusCode();
            return Describe(current, response);
        }

        throw new HttpRequestException($"Exceeded {MaximumRedirects} redirects.");
    }

    private static ProbeResult Describe(Uri finalUri, HttpResponseMessage response)
    {
        var content = response.Content.Headers;
        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;

        long? totalLength = null;
        if (supportsRanges && ContentRange.TryParse(content.ContentRange?.ToString(), out var range))
        {
            if (range.Start != 0 || range.End != 0) throw new InvalidDataException("探測回應的位元組範圍不正確。");
            totalLength = range.TotalLength;
        }
        else if (!supportsRanges)
        {
            totalLength = content.ContentLength;
        }

        var validator = response.Headers.ETag is { IsWeak: false } tag
            ? tag.ToString() : content.LastModified?.ToString("R") ?? string.Empty;

        var fileName = SuggestedFileName.FromContentDisposition(content.ContentDisposition?.ToString())
            ?? SuggestedFileName.FromUri(finalUri);

        return new ProbeResult
        {
            FinalUri = finalUri,
            TotalLength = totalLength,
            // Resume also needs a validator; without one a restarted transfer cannot tell
            // whether the bytes already on disk belong to the file now being served.
            SupportsRanges = supportsRanges && totalLength is > 0 && validator.Length > 0,
            Validator = validator,
            FileName = fileName,
            IsEncoded = content.ContentEncoding.Count > 0,
        };
    }

    private static bool IsRedirect(HttpStatusCode status) => status
        is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;
}
