using System.Net;

namespace Downlism.Core.Http;

/// <summary>Builds the <see cref="HttpClient"/> used for transfers.</summary>
/// <remarks>
/// One client per site profile, not one per download: a busy queue with a client each
/// exhausts sockets, while a shared client keeps the connection pool useful.
/// </remarks>
public static class DownloadHttpClientFactory
{
    public static HttpClient Create(int maximumConnectionsPerServer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConnectionsPerServer);

        var handler = new SocketsHttpHandler
        {
            // Range offsets count compressed bytes. With automatic decompression the handler
            // hands back decoded bytes, so every segment would land at the wrong file offset
            // and the finished file would be silently corrupt.
            AutomaticDecompression = DecompressionMethods.None,

            // Redirects are followed by hand so each hop's cookies can be collected and the
            // final URL is available for naming the file.
            AllowAutoRedirect = false,

            MaxConnectionsPerServer = maximumConnectionsPerServer,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseCookies = false,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            // Long transfers must not trip an overall request timeout; stalls are detected
            // per-read by the transfer loop instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Pins a request to HTTP/1.1. Over HTTP/2 the parallel connections are multiplexed onto
    /// one TCP connection sharing a single congestion window, so segmented downloading gains
    /// nothing while still paying the per-segment overhead.
    /// </summary>
    public static void PinToHttp11(HttpRequestMessage request)
    {
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    }
}
