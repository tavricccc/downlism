using Downlism.Core.Downloads;

namespace Downlism.Core.Media;

internal static class ToolDownload
{
    internal static async Task FetchAsync(HttpClient client, Uri uri, string directory, string fileName,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // Publish only after a complete transfer; a failed refresh must not delete a working tool.
        var staging = Path.Combine(directory, ".staging");
        System.IO.Directory.CreateDirectory(staging);
        var stagedFile = Path.Combine(staging, fileName);
        File.Delete(stagedFile);
        var request = new DownloadRequest
        {
            Uri = uri,
            Directory = staging,
            FileName = fileName,
            SortIntoCategories = false,
            Connections = 8,
        };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await new DownloadEngine(client).RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(result.Path, Path.Combine(directory, fileName), overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 2 && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or TimeoutException or IOException)
            {
                // The engine checkpoints every segment. Retry only the missing bytes, rather
                // than discarding successful parallel work when a CDN connection is reset.
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
