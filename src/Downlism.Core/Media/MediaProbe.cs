using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Downlism.Core.Downloads;

namespace Downlism.Core.Media;

/// <summary>Asks yt-dlp which qualities a media page actually offers.</summary>
public sealed class MediaProbe(MediaTools tools)
{
    public async Task<MediaFormats> RunAsync(
        DownloadRequest request,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        await tools.EnsureYtDlpAsync(status, progress: null, cancellationToken).ConfigureAwait(false);
        status?.Invoke("正在讀取可用品質");

        using var process = new Process
        {
            StartInfo = BuildStartInfo(request),
            EnableRaisingEvents = true,
        };

        var errors = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (sender, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            errors.Enqueue(args.Data.Trim());
            while (errors.Count > 8) errors.TryDequeue(out _);
        };

        process.Start();
        process.BeginErrorReadLine();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        var json = await output.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var message = errors.LastOrDefault(line => line.Contains("ERROR:", StringComparison.Ordinal))
                ?? errors.LastOrDefault()
                ?? "yt-dlp 無法讀取這個來源的品質。";
            throw new MediaDownloadException(message.Replace("ERROR:", string.Empty).Trim());
        }

        return MediaFormats.Parse(json);
    }

    private ProcessStartInfo BuildStartInfo(DownloadRequest request)
    {
        var info = new ProcessStartInfo(tools.YtDlpPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in new[] { "--no-playlist", "--dump-single-json", "--skip-download" })
        {
            info.ArgumentList.Add(argument);
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
            info.ArgumentList.Add("--add-header");
            info.ArgumentList.Add("Cookie:" + request.Cookies);
        }

        info.ArgumentList.Add(request.PageUrl ?? request.Uri.AbsoluteUri);
        return info;
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
        }
    }
}
