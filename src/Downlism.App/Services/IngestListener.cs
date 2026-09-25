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
    Action<CaptureRequest> onCapture,
    Func<Core.Settings.AppSettings> settings,
    string? pipeName = null) : IDisposable
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
                await using var server = IngestPipe.CreateServer(pipeName);
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
        if (!message.TryGetUri(out var uri))
        {
            return IngestReply.Rejected("Only http, https and magnet links are accepted.");
        }

        var current = settings();
        var kind = message.KindFor(uri);

        // Handed on rather than started here. The browser is waiting on this reply and must not
        // be made to wait on a person, so the capture is always accepted and what happens to it
        // is decided in the window that opens next.
        var request = new DownloadRequest
        {
            Uri = uri,
            Kind = kind,
            // Only carried for media. Handing a page address to the other two engines would
            // make them download the page instead of the thing on it.
            PageUrl = kind == TransferKind.Media ? message.PageUrl : null,
            Directory = DownloadFolder(current),
            // Browser handovers are sorted the same way as pasted links; a file arriving from
            // Chrome should not land somewhere different from the same file pasted by hand.
            SortIntoCategories = current.SortIntoCategories,
            // A torrent names itself, and the browser's guess for one would be the .torrent
            // file rather than its contents. For media the name is only a label until yt-dlp
            // reports the real one, and the page title it carries beats a manifest URL.
            FileName = kind == TransferKind.Torrent || string.IsNullOrWhiteSpace(message.FileName)
                ? null
                : message.FileName,
            Connections = current.Connections,
            BytesPerSecond = current.BytesPerSecond,
            ReadTimeoutSeconds = current.ReadTimeoutSeconds,
            CategoryRules = current.CategoryRules,
            Cookies = message.Cookies,
            Referrer = message.Referrer,
            UserAgent = message.UserAgent,
        };

        onCapture(new CaptureRequest(request, message.TotalBytes));
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

    /// <summary>The folder the user last chose, falling back to the shell's Downloads folder.</summary>
    public static string DownloadFolder(Core.Settings.AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloadFolder) ? DownloadFolder() : settings.DownloadFolder;

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

/// <summary>
/// A download that has been captured but not yet started, plus what the browser claimed about
/// its size. The size is a hint for the window to show and never reaches an engine: the
/// servers that lie about Content-Length are the same ones that lie about everything else.
/// </summary>
public sealed record CaptureRequest(DownloadRequest Request, long ExpectedBytes);
