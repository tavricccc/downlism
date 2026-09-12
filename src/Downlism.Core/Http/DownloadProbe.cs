using System.Net;

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
    public async Task<ProbeResult> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;

        for (var hop = 0; hop <= MaximumRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            DownloadHttpClientFactory.PinToHttp11(request);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return Describe(current, response);
        }

        throw new HttpRequestException($"Exceeded {MaximumRedirects} redirects for {uri}.");
    }

    private static ProbeResult Describe(Uri finalUri, HttpResponseMessage response)
    {
        var content = response.Content.Headers;
        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;

        long? totalLength = null;
        if (supportsRanges && ContentRange.TryParse(content.ContentRange?.ToString(), out var range))
        {
            totalLength = range.TotalLength;
        }
        else if (!supportsRanges)
        {
            totalLength = content.ContentLength;
        }

        var validator = response.Headers.ETag?.Tag
            ?? content.LastModified?.ToString("R")
            ?? string.Empty;

        var fileName = SuggestedFileName.FromContentDisposition(content.ContentDisposition?.ToString())
            ?? SuggestedFileName.FromUri(finalUri);

        return new ProbeResult
        {
            FinalUri = finalUri,
            TotalLength = totalLength,
            // Resume also needs a validator; without one a restarted transfer cannot tell
            // whether the bytes already on disk belong to the file now being served.
            SupportsRanges = supportsRanges && totalLength is > 0,
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
