using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Downlism.Tests;

/// <summary>
/// A minimal HTTP/1.1 origin server for engine tests.
/// </summary>
/// <remarks>
/// Built on <see cref="TcpListener"/> rather than <c>HttpListener</c>, which needs a URL ACL
/// reservation and therefore an elevated prompt on a normal developer machine. Serving the
/// bytes by hand also makes it possible to reproduce the behaviour that actually matters here:
/// refusing ranges, changing the entity mid-transfer, or cutting a connection part way.
/// </remarks>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    public TestHttpServer(byte[] content)
    {
        Content = content;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = AcceptAsync(_shutdown.Token);
    }

    public byte[] Content { get; set; }

    public int Port { get; }

    public Uri Uri => new($"http://127.0.0.1:{Port}/payload.bin");

    /// <summary>When false the server answers 200 with the whole body and no <c>Accept-Ranges</c>.</summary>
    public bool SupportsRanges { get; set; } = true;

    public string ETag { get; set; } = "\"v1\"";

    /// <summary>Optional <c>Content-Disposition</c> value sent with every response.</summary>
    public string? ContentDisposition { get; set; }

    /// <summary>Drops the connection after writing this many bytes of a body, then resets to null.</summary>
    public int? TruncateAfterBytes { get; set; }

    /// <summary>Ranged requests observed so far, as (start, end) pairs.</summary>
    public List<(long Start, long End)> RangeRequests { get; } = [];

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        var clients = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                clients.Add(ServeAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null) return;

                var range = ParseRange(request);
                if (range is not null)
                {
                    lock (RangeRequests) RangeRequests.Add(range.Value);
                }

                await RespondAsync(stream, range, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RespondAsync(NetworkStream stream, (long Start, long End)? range, CancellationToken cancellationToken)
    {
        var body = Content;
        var header = new StringBuilder();

        if (SupportsRanges && range is not null)
        {
            var start = Math.Clamp(range.Value.Start, 0, Math.Max(0, body.Length - 1));
            var end = Math.Clamp(range.Value.End, start, body.Length - 1);
            var length = end - start + 1;

            header.Append("HTTP/1.1 206 Partial Content\r\n");
            header.Append($"Content-Range: bytes {start}-{end}/{body.Length}\r\n");
            header.Append($"Content-Length: {length}\r\n");
            AppendCommonHeaders(header);

            await WriteAsync(stream, header.ToString(), body.AsMemory((int)start, (int)length), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        header.Append("HTTP/1.1 200 OK\r\n");
        header.Append($"Content-Length: {body.Length}\r\n");
        AppendCommonHeaders(header);

        await WriteAsync(stream, header.ToString(), body, cancellationToken).ConfigureAwait(false);
    }

    private void AppendCommonHeaders(StringBuilder header)
    {
        header.Append("Content-Type: application/octet-stream\r\n");
        if (SupportsRanges) header.Append("Accept-Ranges: bytes\r\n");
        if (!string.IsNullOrEmpty(ETag)) header.Append($"ETag: {ETag}\r\n");
        if (!string.IsNullOrEmpty(ContentDisposition)) header.Append($"Content-Disposition: {ContentDisposition}\r\n");
        header.Append("Connection: close\r\n\r\n");
    }

    private async Task WriteAsync(NetworkStream stream, string header, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);

        // Skip bodies no larger than the cut, so the one-byte probe response does not consume
        // a truncation that was meant for a segment.
        var truncate = TruncateAfterBytes;
        if (truncate is not null && body.Length > truncate.Value)
        {
            TruncateAfterBytes = null;
            var cut = Math.Min(truncate.Value, body.Length);
            await stream.WriteAsync(body[..cut], cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var used = 0;

        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            used += read;

            var text = Encoding.ASCII.GetString(buffer, 0, used);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }

        return null;
    }

    private static (long Start, long End)? ParseRange(string request)
    {
        foreach (var line in request.Split("\r\n"))
        {
            if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line["Range:".Length..].Trim();
            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = value["bytes=".Length..].Split('-', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var start)) continue;

            var end = long.TryParse(parts[1], out var parsed) ? parsed : long.MaxValue;
            return (start, end);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }
}
