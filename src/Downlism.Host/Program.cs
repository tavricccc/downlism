using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Downlism.Core.Ingest;

namespace Downlism.Host;

/// <summary>
/// The native messaging host: a thin bridge from the browser extension to the running app.
/// </summary>
/// <remarks>
/// Deliberately does no downloading of its own. The browser owns this process and may kill it
/// the moment the extension's service worker is recycled, which is far shorter than the life
/// of a transfer. Everything it receives is forwarded over a named pipe to the app, which is
/// started if it is not already running.
/// </remarks>
internal static class Program
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main()
    {
        try
        {
            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();

            while (true)
            {
                var message = await ReadBrowserMessageAsync(input).ConfigureAwait(false);
                if (message is null) return 0;

                var reply = await ForwardAsync(message).ConfigureAwait(false);
                await WriteBrowserMessageAsync(output, reply).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // Nothing here has a console to complain to, and a crash dialog behind a browser
            // would be invisible. Fail quietly and let the extension notice the closed pipe.
            Trace.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<IngestReply> ForwardAsync(IngestMessage message)
    {
        if (message.Ping) return IngestReply.Ok();
        if (!message.TryGetUri(out _)) return IngestReply.Rejected("Only http and https downloads are accepted.");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await using var pipe = IngestPipe.CreateClient();
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
                await IngestPipe.WriteAsync(pipe, message, IngestJsonContext.Default.IngestMessage, CancellationToken.None)
                    .ConfigureAwait(false);

                var reply = await IngestPipe
                    .ReadAsync(pipe, IngestJsonContext.Default.IngestReply, CancellationToken.None)
                    .ConfigureAwait(false);

                return reply ?? IngestReply.Ok();
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                // The app is not listening yet. Start it once, then try the pipe again.
                if (attempt == 1 || !TryStartApp()) return IngestReply.Rejected("Downlism is not running.");
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }

        return IngestReply.Rejected("Downlism is not running.");
    }

    /// <summary>
    /// Launches the app from this executable's own directory. The path is never read from the
    /// registry or from the message, so a tampered registration cannot turn the browser into
    /// a launcher for an arbitrary program.
    /// </summary>
    private static bool TryStartApp()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Downlism.App.exe");
        if (!File.Exists(path)) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            return process is not null;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Reads Chrome's framing: a little-endian 32-bit length followed by UTF-8 JSON.</summary>
    private static async Task<IngestMessage?> ReadBrowserMessageAsync(Stream input)
    {
        var prefix = new byte[4];
        if (!await ReadExactlyAsync(input, prefix).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumMessageBytes) return null;

        var payload = new byte[length];
        if (!await ReadExactlyAsync(input, payload).ConfigureAwait(false)) return null;

        return JsonSerializer.Deserialize(payload, IngestJsonContext.Default.IngestMessage);
    }

    private static async Task WriteBrowserMessageAsync(Stream output, IngestReply reply)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(reply, IngestJsonContext.Default.IngestReply);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

        await output.WriteAsync(prefix).ConfigureAwait(false);
        await output.WriteAsync(payload).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset)).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }
}
