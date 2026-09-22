using System.IO.Compression;
using Downlism.Core.Downloads;

namespace Downlism.Core.Media;

/// <summary>
/// Provisions yt-dlp, its JavaScript runtime, and the FFmpeg tools on first use.
/// </summary>
/// <remarks>
/// yt-dlp and ffmpeg are not shipped inside the installer. ffmpeg alone is larger than the
/// rest of Downlism put together, and both carry licences that would have to be reproduced and
/// tracked through every release; fetching them on first use keeps that out of the installer
/// and out of the update path.
///
/// The extraction rules for every video site change from week to week, which is exactly why
/// none of them are implemented here: yt-dlp has thousands of contributors following those
/// changes and a release most days. A copy that sits still for a month stops working on the
/// popular sites, so a stale binary is refreshed in the background rather than trusted.
///
/// The fetching itself goes through <see cref="DownloadEngine"/> rather than a plain
/// <c>GetStreamAsync</c>. These are the two largest downloads a new installation performs, over
/// a CDN that is perfectly capable of stalling halfway; the engine already has the per-read
/// stall timeout, the resume, the redirect walk and the segmenting, and a second, worse copy of
/// that logic here would be the one that hangs forever with no way to tell.
/// </remarks>
public sealed class MediaTools
{
    private readonly HttpClient _client;
    internal string ToolsDirectory { get; }

    public MediaTools(HttpClient client) : this(client, Directory) { }

    internal MediaTools(HttpClient client, string directory)
    {
        _client = client;
        ToolsDirectory = directory;
    }

    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    /// <summary>
    /// yt-dlp's own ffmpeg builds, chosen over the upstream ones because they carry the
    /// patches yt-dlp expects and are published at a URL that does not change per release.
    /// </summary>
    private const string FfmpegUrl =
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    /// <summary>How old a yt-dlp binary may get before it is fetched again.</summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(14);

    // One transfer at a time may provision. Two media downloads started together would
    // otherwise race to write the same executable, and the loser fails on a locked file.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Downlism",
        "tools");

    public string YtDlpPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");

    public string FfmpegPath => Path.Combine(ToolsDirectory, "ffmpeg.exe");

    public string DenoPath => Path.Combine(ToolsDirectory, "deno.exe");

    public bool IsReady => File.Exists(YtDlpPath) && File.Exists(DenoPath)
        && File.Exists(FfmpegPath) && File.Exists(Path.Combine(ToolsDirectory, "ffprobe.exe"));

    /// <summary>Makes sure yt-dlp is present before probing a page or downloading it.</summary>
    public async Task EnsureYtDlpAsync(
        Action<string>? note,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(ToolsDirectory);

            if (!File.Exists(YtDlpPath))
            {
                note?.Invoke("正在下載 yt-dlp（約 17 MB），只需要這一次");
                await FetchAsync(YtDlpUrl, "yt-dlp.exe", progress, cancellationToken).ConfigureAwait(false);
            }
            else if (IsStale(YtDlpPath))
            {
                note?.Invoke("正在更新 yt-dlp");
                await TryRefreshAsync(progress, cancellationToken).ConfigureAwait(false);
            }

            // Official yt-dlp.exe bundles EJS, but not the runtime required by YouTube.
            if (!File.Exists(DenoPath))
            {
                note?.Invoke("正在下載 YouTube 解析所需的 Deno 執行環境");
                await FetchArchiveAsync(DenoUrl, "deno.zip", ["deno.exe"], progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes sure both executables are present, reporting what it is doing so a first run does
    /// not look like a download that has simply stopped.
    /// </summary>
    /// <param name="note">Called with the phase, in the words the row will show.</param>
    /// <param name="progress">
    /// Forwarded from the transfer that triggered provisioning, so the first video download
    /// shows a moving bar for the two hundred megabytes it spends before it starts.
    /// </param>
    public async Task EnsureAsync(
        Action<string>? note,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await EnsureYtDlpAsync(note, progress, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FfmpegPath) || !File.Exists(Path.Combine(ToolsDirectory, "ffprobe.exe")))
            {
                note?.Invoke("正在下載 ffmpeg（約 180 MB），只需要這一次");
                await FetchArchiveAsync(FfmpegUrl, "ffmpeg.zip", ["ffprobe.exe", "ffmpeg.exe"], progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsStale(string path)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > RefreshAfter;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// A refresh is best effort. An out-of-date yt-dlp still works on most sites, and failing
    /// the transfer because GitHub was unreachable would trade a small problem for a total one.
    /// </summary>
    private async Task TryRefreshAsync(IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            await FetchAsync(YtDlpUrl, "yt-dlp.exe", progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the copy already on disk and mark it fresh, so a network that stays down
            // does not make every media download start with a doomed refresh.
            try
            {
                File.SetLastWriteTimeUtc(YtDlpPath, DateTime.UtcNow);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Downloads one file into the tools folder under exactly the name given.
    /// </summary>
    private Task FetchAsync(
        string url,
        string fileName,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
        => ToolDownload.FetchAsync(_client, new Uri(url), ToolsDirectory, fileName, progress, cancellationToken);

    private async Task FetchArchiveAsync(string url, string archiveName, string[] executables,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var archive = Path.Combine(ToolsDirectory, archiveName);

        try
        {
            await FetchAsync(url, archiveName, progress, cancellationToken).ConfigureAwait(false);

            using var zip = ZipFile.OpenRead(archive);
            // The build nests everything under ffmpeg-.../bin/, and the folder name carries the
            // build date, so the entries are matched on their file name rather than a path.
            foreach (var wanted in executables)
            {
                var entry = zip.Entries.FirstOrDefault(candidate =>
                    string.Equals(Path.GetFileName(candidate.FullName), wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{archiveName} 壓縮檔裡找不到 {wanted}。");

                cancellationToken.ThrowIfCancellationRequested();
                var staged = Path.Combine(ToolsDirectory, ".staging", wanted);
                entry.ExtractToFile(staged, overwrite: true);
                File.Move(staged, Path.Combine(ToolsDirectory, wanted), overwrite: true);
            }
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
