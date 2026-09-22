using System.Net;
using Downlism.Core.Downloads;

namespace Downlism.Core.Http;

/// <summary>For small metadata such as .torrent files, never an unbounded GetByteArrayAsync.</summary>
public static class BoundedHttpDownload
{
    public static async Task<byte[]> ReadAsync(HttpClient client, DownloadRequest source, int maximumBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var current = source.Uri;
        for (var hop = 0; hop <= 10; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            DownloadHeaders.Apply(request, source);
            using var response = await DownloadHeaders.SendAsync(client, request, source.ReadTimeoutSeconds, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme is not ("http" or "https") || (current.Scheme == "https" && next.Scheme == "http"))
                    throw new HttpRequestException("拒絕不安全的重新導向。");
                current = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("來源中繼資料超出允許大小。");
            using var output = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[Math.Min(64 * 1024, maximumBytes)];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            while (true)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(source.ReadTimeoutSeconds, 5, 600)));
                int read;
                try { read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("讀取來源中繼資料逾時。"); }
                if (read == 0) return output.ToArray();
                if (output.Length + read > maximumBytes) throw new InvalidDataException("來源中繼資料超出允許大小。");
                output.Write(buffer, 0, read);
            }
        }
        throw new HttpRequestException("重新導向次數過多。");
    }
}
