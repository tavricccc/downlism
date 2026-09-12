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

    /// <summary>How long to wait on an app that should already be listening.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait after launching it, which includes a cold WinUI start.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

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
        // A ping asks whether Downlism is reachable, so it has to actually reach it. Answering
        // yes without checking is worse than saying nothing: the extension reports "connected"
        // while every download it hands over is dropped.
        if (message.Ping)
        {
            return await SendAsync(message, ProbeTimeout).ConfigureAwait(false)
                ?? IngestReply.Rejected("Downlism is not running.");
        }

        if (!message.TryGetUri(out _)) return IngestReply.Rejected("Only http and https downloads are accepted.");

        // Try the running app first; only pay for a cold start when there is nothing listening.
        var reply = await SendAsync(message, ProbeTimeout).ConfigureAwait(false);
        if (reply is not null) return reply;

        if (!TryStartApp()) return IngestReply.Rejected("Downlism is not running.");

        return await SendAsync(message, StartupTimeout).ConfigureAwait(false)
            ?? IngestReply.Rejected("Downlism did not start in time.");
    }

    /// <summary>Sends one message, returning null when nothing is listening.</summary>
    private static async Task<IngestReply?> SendAsync(IngestMessage message, TimeSpan timeout)
    {
        try
        {
            await using var pipe = IngestPipe.CreateClient();
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);
            await IngestPipe.WriteAsync(pipe, message, IngestJsonContext.Default.IngestMessage, CancellationToken.None)
                .ConfigureAwait(false);

            return await IngestPipe
                .ReadAsync(pipe, IngestJsonContext.Default.IngestReply, CancellationToken.None)
                .ConfigureAwait(false) ?? IngestReply.Ok();
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
