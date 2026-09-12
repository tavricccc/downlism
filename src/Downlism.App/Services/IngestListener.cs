using Downlism.Core.Downloads;
using Downlism.Core.Ingest;

namespace Downlism.App.Services;

/// <summary>
/// Accepts downloads handed over by the browser, through the native messaging host.
/// </summary>
/// <remarks>
/// Each connection is served and closed: the host is a short-lived process that the browser
/// may terminate at any moment, so a long-lived session would spend most of its life holding
/// a dead pipe.
/// </remarks>
public sealed class IngestListener(
    DownloadQueue queue,
    Action<DownloadJob> onAccepted,
    Func<Core.Settings.AppSettings> settings) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public void Start() => _loop = AcceptAsync(_shutdown.Token);

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = IngestPipe.CreateServer();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var message = await IngestPipe
                    .ReadAsync(server, IngestJsonContext.Default.IngestMessage, cancellationToken)
                    .ConfigureAwait(false);

                var reply = message is null ? IngestReply.Rejected("Empty request.") : Accept(message);

                await IngestPipe.WriteAsync(server, reply, IngestJsonContext.Default.IngestReply, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                // A malformed or abandoned connection must not take the listener down with it.
            }
            catch (Exception)
            {
                // Nothing else is allowed to end the loop either. A listener that dies here is
                // invisible: the app keeps working, the browser keeps handing downloads over,
                // and every one of them is silently refused. Pause briefly so a failure that
                // repeats immediately cannot spin the CPU.
                LastFailure = DateTimeOffset.UtcNow;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>When the listener last failed for a reason it did not expect, if ever.</summary>
    public DateTimeOffset? LastFailure { get; private set; }

    private IngestReply Accept(IngestMessage message)
    {
        if (message.Ping) return IngestReply.Ok();

        // The URL arrives from a web page by way of the extension, so it is validated here
        // rather than trusted because it came through a named pipe.
        if (!message.TryGetUri(out var uri)) return IngestReply.Rejected("Only http and https downloads are accepted.");

        var current = settings();

        var job = queue.Add(new DownloadRequest
        {
            Uri = uri,
            Directory = DownloadFolder(),
            // Browser handovers are sorted the same way as pasted links; a file arriving from
            // Chrome should not land somewhere different from the same file pasted by hand.
            SortIntoCategories = current.SortIntoCategories,
            FileName = string.IsNullOrWhiteSpace(message.FileName) ? null : message.FileName,
            Connections = current.Connections,
            BytesPerSecond = current.BytesPerSecond,
            Cookies = message.Cookies,
            Referrer = message.Referrer,
            UserAgent = message.UserAgent,
        });

        onAccepted(job);
        return IngestReply.Ok();
    }

    /// <summary>
    /// Resolves the shell's Downloads folder, falling back to the profile path when the known
    /// folder has been redirected somewhere unavailable.
    /// </summary>
    public static string DownloadFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads");
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _shutdown.Dispose();
    }
}
