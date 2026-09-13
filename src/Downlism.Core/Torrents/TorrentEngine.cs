using System.Diagnostics;
using System.Net;
using Downlism.Core.Downloads;
using MonoTorrent;
using MonoTorrent.Client;

namespace Downlism.Core.Torrents;

/// <summary>
/// Downloads magnet links and .torrent files through one shared BitTorrent session.
/// </summary>
/// <remarks>
/// The technical selection document ruled BitTorrent out on the grounds that it shares nothing
/// with the rest of the product. That was true of the engine and false of everything around
/// it: the queue, the concurrency limit, the pause and resume buttons, the list that survives
/// a restart, the tray notification and the segmented progress ribbon all apply unchanged. The
/// ribbon in particular fits better here than anywhere else — a swarm fills a file in scattered
/// pieces, and that is precisely what the control was built to show.
///
/// One session serves every torrent. A second <see cref="ClientEngine"/> would mean a second
/// listening port, a second DHT table and two halves of one peer pool that never meet.
///
/// Seeding stops the moment a torrent completes. Continuing to upload is a decision about
/// somebody's bandwidth and, in some places, their legal exposure; it is not something a
/// download manager may start doing on its own.
/// </remarks>
public sealed class TorrentEngine : ITransferEngine, IAsyncDisposable
{
    /// <summary>
    /// How many segments the ribbon is divided into. The real piece count runs to tens of
    /// thousands, which at screen resolution would be a solid block that says nothing.
    /// </summary>
    private const int RibbonSegments = 16;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientEngine? _engine;
    private long _bytesPerSecond;

    public TorrentEngine(HttpClient client) => _client = client;

    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Downlism",
        "torrents");

    /// <summary>
    /// The rate ceiling is a property of the session, not of one transfer: peers are shared and
    /// the socket layer throttles in one place. The newest choice wins.
    /// </summary>
    public async Task SetRateLimitAsync(long bytesPerSecond)
    {
        Interlocked.Exchange(ref _bytesPerSecond, bytesPerSecond);

        var engine = _engine;
        if (engine is null) return;

        await engine.UpdateSettingsAsync(BuildSettings(bytesPerSecond)).ConfigureAwait(false);
    }

    public async Task<DownloadResult> RunAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var engine = await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);

        // Torrents bring their own folder structure and often their own extensions, so the
        // usual by-type sorting has nothing to work with. They get one folder of their own.
        var directory = request.SortIntoCategories
            ? Path.Combine(request.Directory, "BitTorrent")
            : request.Directory;

        Directory.CreateDirectory(directory);

        var manager = await AddAsync(engine, request, directory, cancellationToken).ConfigureAwait(false);

        try
        {
            await manager.StartAsync().ConfigureAwait(false);

            if (!manager.HasMetadata)
            {
                // A magnet link is only an identifier. The file list has to be fetched from the
                // swarm before a single byte of content can be asked for, and on a cold torrent
                // that takes long enough that silence would read as a stall.
                Report(progress, manager, started, "正在向 DHT 取得種子資訊");
                await manager.WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);
            }

            while (!manager.Complete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (manager.State == TorrentState.Error)
                {
                    throw new TorrentException(
                        manager.Error?.Exception?.Message ?? "這個種子無法下載。", manager.Error?.Exception);
                }

                Report(progress, manager, started, null);
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            Report(progress, manager, started, null);

            // Stopping here is what ends the seeding. The data is already on disk and hashed;
            // what stops is giving it away.
            await manager.StopAsync().ConfigureAwait(false);

            var path = manager.ContainingDirectory ?? Path.Combine(directory, manager.Name);
            var size = manager.Torrent?.Size ?? 0;

            return new DownloadResult(path, size, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException)
        {
            // A paused torrent keeps every finished piece. MonoTorrent writes its own fast
            // resume beside the cache, so restarting picks up the bitfield rather than
            // rehashing the whole file.
            await StopQuietlyAsync(manager).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await StopQuietlyAsync(manager).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resolves a request into a torrent the session can run: a magnet link directly, or a
    /// .torrent file fetched over HTTP first.
    /// </summary>
    private async Task<TorrentManager> AddAsync(
        ClientEngine engine,
        DownloadRequest request,
        string directory,
        CancellationToken cancellationToken)
    {
        if (request.Uri.Scheme == "magnet")
        {
            var link = MagnetLink.FromUri(request.Uri);
            return Existing(engine, link.InfoHashes) ?? await engine.AddAsync(link, directory).ConfigureAwait(false);
        }

        var bytes = await _client.GetByteArrayAsync(request.Uri, cancellationToken).ConfigureAwait(false);

        if (!Torrent.TryLoad(bytes, out var torrent))
        {
            // The usual cause is a tracker answering with an HTML error page under a .torrent
            // URL, which would otherwise surface as an unreadable parser error.
            throw new TorrentException("這個網址回應的不是種子檔。");
        }

        return Existing(engine, torrent.InfoHashes) ?? await engine.AddAsync(torrent, directory).ConfigureAwait(false);
    }

    /// <summary>
    /// The same torrent added twice is one torrent. Adding it again would be rejected by the
    /// session, and a retry after a failure has to find the manager it left behind.
    /// </summary>
    private static TorrentManager? Existing(ClientEngine engine, InfoHashes hashes) =>
        engine.Torrents.FirstOrDefault(manager => manager.InfoHashes == hashes);

    private static void Report(
        IProgress<DownloadProgress>? progress,
        TorrentManager manager,
        long started,
        string? note)
    {
        if (progress is null) return;

        var total = manager.Torrent?.Size;
        var completed = total is > 0 ? (long)(manager.Progress / 100 * total.Value) : 0;
        var rate = manager.Monitor.DownloadRate;

        progress.Report(new DownloadProgress(
            completed,
            total,
            rate,
            BuildSegments(manager),
            note ?? Describe(manager)));
    }

    /// <summary>
    /// Folds the piece bitfield into a fixed number of buckets. Each bucket is a real byte
    /// range of the file and fills with the pieces the swarm has actually delivered, so the
    /// ribbon shows the scattered, out-of-order shape of a torrent instead of a smooth bar.
    /// </summary>
    private static IReadOnlyList<Segment> BuildSegments(TorrentManager manager)
    {
        if (manager.Torrent is not { Size: > 0 } torrent) return [];

        var bitfield = manager.Bitfield;
        if (bitfield.Length == 0) return [];

        var buckets = Math.Min(RibbonSegments, bitfield.Length);
        var segments = new List<Segment>(buckets);
        var pieceLength = torrent.PieceLength;

        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var firstPiece = (int)((long)bitfield.Length * bucket / buckets);
            var lastPiece = (int)((long)bitfield.Length * (bucket + 1) / buckets) - 1;
            if (lastPiece < firstPiece) continue;

            var start = (long)firstPiece * pieceLength;
            var end = Math.Min((long)(lastPiece + 1) * pieceLength, torrent.Size) - 1;

            var have = 0;
            for (var piece = firstPiece; piece <= lastPiece; piece++)
            {
                if (bitfield[piece]) have++;
            }

            // Clamped because the final piece of a torrent is short, and counting it whole
            // would draw the last bucket as more than full.
            var completed = Math.Min((long)have * pieceLength, end - start + 1);
            segments.Add(new Segment(start, end, completed));
        }

        return segments;
    }

    private static string Describe(TorrentManager manager) => manager.State switch
    {
        TorrentState.Metadata => "正在向 DHT 取得種子資訊",
        TorrentState.Hashing or TorrentState.HashingPaused => "正在檢查已下載的片段",
        TorrentState.FetchingHashes => "正在取得片段雜湊",
        TorrentState.Starting => "正在連線到 tracker",
        TorrentState.Downloading when manager.Peers.Seeds + manager.Peers.Leechs == 0 => "正在尋找節點",
        TorrentState.Downloading => $"{manager.Peers.Seeds} 個種子 · {manager.Peers.Leechs} 個節點",
        TorrentState.Seeding => "已完成",
        _ => "BitTorrent",
    };

    private async Task<ClientEngine> EnsureEngineAsync(CancellationToken cancellationToken)
    {
        if (_engine is { } running) return running;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _engine ??= new ClientEngine(BuildSettings(Interlocked.Read(ref _bytesPerSecond)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static EngineSettings BuildSettings(long bytesPerSecond) => new EngineSettingsBuilder
    {
        CacheDirectory = CacheDirectory,
        // Port zero, so Windows assigns one instead of Downlism colliding with whatever else
        // on the machine has claimed the customary BitTorrent range.
        ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, 0) },
        DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
        AutoSaveLoadFastResume = true,
        AutoSaveLoadMagnetLinkMetadata = true,
        AllowPortForwarding = true,
        AllowLocalPeerDiscovery = true,
        MaximumConnections = 120,
        MaximumDownloadRate = Clamp(bytesPerSecond),
        // Uploading is capped rather than disabled: most swarms starve a peer that gives
        // nothing back, so refusing to upload at all would make the download itself slower.
        MaximumUploadRate = 256 * 1024,
    }.ToSettings();

    /// <summary>MonoTorrent's rate ceilings are 32-bit, and zero already means unlimited.</summary>
    private static int Clamp(long bytesPerSecond) =>
        bytesPerSecond is <= 0 or > int.MaxValue ? 0 : (int)bytesPerSecond;

    private static async Task StopQuietlyAsync(TorrentManager manager)
    {
        try
        {
            // Bounded, because a stop waits on tracker announcements that may never answer and
            // the person has already asked for this transfer to end.
            await manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The session is being torn down; a failure to say goodbye politely changes nothing
            // about the data already on disk.
        }
    }

    public async ValueTask DisposeAsync()
    {
        var engine = Interlocked.Exchange(ref _engine, null);
        if (engine is null)
        {
            _gate.Dispose();
            return;
        }

        try
        {
            await engine.StopAllAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        engine.Dispose();
        _gate.Dispose();
    }
}

/// <summary>A BitTorrent failure, phrased for the person who pasted the link.</summary>
public sealed class TorrentException(string message, Exception? inner = null) : Exception(message, inner);
