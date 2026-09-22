using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Downlism.Core.Downloads;

namespace Downlism.Core.Media;

/// <summary>
/// Downloads video by driving yt-dlp, and reports its progress as if it were one of our own
/// transfers.
/// </summary>
/// <remarks>
/// The decision recorded in the technical selection document was to shell out rather than
/// implement HLS and DASH here. Nothing about that has changed: a manifest parser is the easy
/// half, and the half that matters — player tokens, signature functions, throttled formats,
/// per-site quirks — changes faster than a release cycle.
///
/// What is implemented here is the part yt-dlp cannot do: making the result look like the rest
/// of Downlism. A video is normally two separate streams that are merged afterwards, so the
/// progress ribbon gets one segment per stream and fills them in the order yt-dlp works
/// through them. That is the honest picture, and it is the same picture the segmented HTTP
/// engine draws.
/// </remarks>
public sealed class MediaEngine(MediaTools tools) : ITransferEngine
{
    /// <summary>
    /// Prefixes chosen so they cannot collide with yt-dlp's own output, which is full of
    /// bracketed tags. Parsing English progress text would break on the first release that
    /// reworded it.
    /// </summary>
    private const string ProgressMarker = "@dl ";

    private const string FileMarker = "@file ";

    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    public async Task<DownloadResult> RunAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var state = new TransferState(progress, started);

        // Provisioning is itself two large downloads, so its progress is forwarded as this
        // transfer's own. A first run otherwise spends several minutes on an empty bar.
        await tools
            .EnsureAsync(state.SetNote, new Progress<DownloadProgress>(state.Forward), cancellationToken)
            .ConfigureAwait(false);

        // The real extension is not known until yt-dlp has picked or converted a format, and by
        // then the directory has to exist, so use the output choice to select its category.
        var categoryHint = request.MediaOutput == MediaOutput.Audio ? "audio.mp3" : "video.mp4";
        var directory = DownloadCategory.DirectoryFor(request.Directory, categoryHint, request.SortIntoCategories, request.CategoryRules);
        System.IO.Directory.CreateDirectory(directory);

        state.SetNote("正在解析來源");

        using var process = new Process
        {
            StartInfo = BuildStartInfo(request, directory),
            EnableRaisingEvents = true,
        };

        var errors = new ConcurrentQueue<string>();

        process.OutputDataReceived += (_, args) => Consume(args.Data, state, errors);
        process.ErrorDataReceived += (_, args) => Consume(args.Data, state, errors);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // yt-dlp spawns ffmpeg, and killing only the parent leaves a muxer holding the
            // output file open, which makes the next resume fail on a locked path.
            KillTree(process);
            throw;
        }

        if (process.ExitCode != 0) throw new MediaDownloadException(Describe(errors));

        var path = state.FinalPath
            ?? throw new MediaDownloadException("yt-dlp 沒有回報輸出檔案，下載可能沒有完成。");

        state.Complete();

        var bytes = File.Exists(path) ? new FileInfo(path).Length : state.Completed;
        return new DownloadResult(path, bytes, Stopwatch.GetElapsedTime(started));
    }

    private ProcessStartInfo BuildStartInfo(DownloadRequest request, string directory)
    {
        var info = new ProcessStartInfo(tools.YtDlpPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // yt-dlp prints file names in the site's own language; without this they arrive as
            // mojibake on a machine whose console code page is not UTF-8.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // A page URL nearly always resolves better than the manifest the sniffer saw: it gives
        // yt-dlp the extractor, the title and the full format list instead of one naked stream.
        var target = request.PageUrl ?? request.Uri.AbsoluteUri;

        string[] arguments =
        [
            "--ignore-config",
            "--js-runtimes", "deno:" + tools.DenoPath,
            "--no-playlist",
            // One line per progress update. The default carriage-return redraw is a single
            // endless line that never raises an OutputDataReceived event.
            "--newline",
            "--no-colors",
            "--no-simulate",
            // --print implies --quiet, and quiet suppresses progress unless it is asked for.
            "--progress",
            "--progress-template",
            ProgressMarker
                + "%(progress.downloaded_bytes)s %(progress.total_bytes)s "
                + "%(progress.total_bytes_estimate)s %(info.format_id)s",
            "--print",
            "after_move:" + FileMarker + "%(filepath)s",
            "--ffmpeg-location", tools.ToolsDirectory,
            "--paths", "home:" + directory,
            // Trimmed to 120 bytes rather than characters: a CJK title can exceed MAX_PATH
            // long before it looks long.
            "-o", "%(title).120B [%(id)s].%(ext)s",
            // The same argument the segmented engine makes, applied to fragments.
            "--concurrent-fragments", Math.Clamp(request.Connections, 1, 16).ToString(CultureInfo.InvariantCulture),
            "--retries", "10",
            "--fragment-retries", "10",
        ];

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var argument in MediaFormat.ArgumentsFor(request)) info.ArgumentList.Add(argument);

        if (request.BytesPerSecond > 0)
        {
            info.ArgumentList.Add("--limit-rate");
            info.ArgumentList.Add(request.BytesPerSecond.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(request.Referrer))
        {
            info.ArgumentList.Add("--referer");
            info.ArgumentList.Add(request.Referrer);
        }

        if (!string.IsNullOrEmpty(request.UserAgent))
        {
            info.ArgumentList.Add("--user-agent");
            info.ArgumentList.Add(request.UserAgent);
        }

        if (!string.IsNullOrEmpty(request.Cookies))
        {
            // Passed as a header rather than through --cookies-from-browser, which would read
            // the whole cookie jar for every site the person is signed in to.
            info.ArgumentList.Add("--add-header");
            info.ArgumentList.Add("Cookie:" + request.Cookies);
        }

        info.ArgumentList.Add(target);
        return info;
    }

    private static void Consume(string? line, TransferState state, ConcurrentQueue<string> errors)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (line.StartsWith(ProgressMarker, StringComparison.Ordinal))
        {
            state.Observe(line.AsSpan(ProgressMarker.Length));
            return;
        }

        if (line.StartsWith(FileMarker, StringComparison.Ordinal))
        {
            state.FinalPath = line[FileMarker.Length..].Trim();
            return;
        }

        // Post-processing has no byte count to report, so the only honest thing to show is
        // which phase the transfer is in.
        if (line.StartsWith("[Merger]", StringComparison.Ordinal)) state.SetNote("正在合併影音軌");
        else if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)) state.SetNote("正在轉出音訊");
        else if (line.StartsWith("[Fixup", StringComparison.Ordinal)) state.SetNote("正在修正檔案");
        else if (line.StartsWith("[VideoConvertor]", StringComparison.Ordinal)) state.SetNote("正在轉檔");
        else if (line.Contains("ERROR:", StringComparison.Ordinal)
            || line.StartsWith("WARNING:", StringComparison.Ordinal))
        {
            errors.Enqueue(line.Trim());
            while (errors.Count > 8) errors.TryDequeue(out _);
        }
    }

    /// <summary>Prefers a real error over the warnings that precede it.</summary>
    private static string Describe(ConcurrentQueue<string> errors)
    {
        var lines = errors.ToArray();
        var error = lines.LastOrDefault(line => line.Contains("ERROR:", StringComparison.Ordinal));
        var message = (error ?? lines.LastOrDefault())?.Replace("ERROR:", string.Empty).Trim();

        return string.IsNullOrEmpty(message) ? "yt-dlp 無法下載這個來源。" : message;
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            // The process finished between the check and the kill, or the tree had already
            // been reaped. Either way there is nothing left to stop.
        }
    }

    /// <summary>
    /// Accumulates what yt-dlp reports into the shape the rest of the app already understands.
    /// </summary>
    private sealed class TransferState(IProgress<DownloadProgress>? progress, long started)
    {
        /// <summary>
        /// Kept in the order the streams are first seen, because that is the order they are
        /// drawn in and it must not change from one frame to the next.
        /// </summary>
        private readonly List<string> _order = [];

        private readonly Dictionary<string, (long Downloaded, long Total)> _streams = [];
        private readonly Lock _sync = new();

        private string? _note;
        private long _lastReportTicks;

        public string? FinalPath { get; set; }

        public long Completed
        {
            get
            {
                lock (_sync) return _streams.Values.Sum(stream => stream.Downloaded);
            }
        }

        public void SetNote(string note)
        {
            lock (_sync) _note = note;
            Report(force: true);
        }

        /// <summary>
        /// Passes a sample from another engine through as this transfer's own, keeping the note
        /// so the row still says which tool is being fetched rather than naming a file.
        /// </summary>
        public void Forward(DownloadProgress sample)
        {
            string? note;
            lock (_sync) note = _note;

            progress?.Report(sample with { Note = note });
        }

        /// <summary>Parses "downloaded total estimate formatId", any of which may be NA.</summary>
        public void Observe(ReadOnlySpan<char> fields)
        {
            Span<Range> parts = stackalloc Range[4];
            if (fields.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries) < 4) return;

            var downloaded = Number(fields[parts[0]]);
            if (downloaded is null) return;

            var total = Number(fields[parts[1]]) ?? Number(fields[parts[2]]) ?? 0;
            var format = fields[parts[3]].ToString();

            lock (_sync)
            {
                if (!_streams.ContainsKey(format)) _order.Add(format);

                // The estimate moves around while a fragmented stream is in flight; keeping the
                // largest figure stops the bar from jumping backwards.
                var previous = _streams.TryGetValue(format, out var existing) ? existing.Total : 0;
                _streams[format] = (downloaded.Value, Math.Max(previous, total));
                _note = null;
            }

            Report(force: false);
        }

        public void Complete() => Report(force: true);

        private static long? Number(ReadOnlySpan<char> value) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        private void Report(bool force)
        {
            if (progress is null) return;

            if (!force)
            {
                // Four frames a second, matching the HTTP engine, so a media row and a file row
                // in the same list do not update at visibly different rates.
                var now = Stopwatch.GetTimestamp();
                var last = Interlocked.Read(ref _lastReportTicks);
                if (Stopwatch.GetElapsedTime(last, now) < ReportInterval) return;
                if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last) return;
            }

            long completed;
            long total;
            string? note;
            var segments = new List<Segment>();

            lock (_sync)
            {
                var offset = 0L;
                foreach (var format in _order)
                {
                    var stream = _streams[format];

                    // A stream whose size is still unknown is drawn at whatever it has already
                    // written, which makes it a growing bar rather than an absent one.
                    var length = Math.Max(stream.Total, stream.Downloaded);
                    if (length <= 0) continue;

                    segments.Add(new Segment(offset, offset + length - 1, stream.Downloaded));
                    offset += length;
                }

                completed = _streams.Values.Sum(stream => stream.Downloaded);
                total = offset;
                note = _note;
            }

            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var rate = elapsed > 0 ? completed / elapsed : 0;

            progress.Report(new DownloadProgress(completed, total > 0 ? total : null, rate, segments, note));
        }
    }
}

/// <summary>A failure yt-dlp reported, already phrased for the person who asked for the video.</summary>
public sealed class MediaDownloadException(string message) : Exception(message);
