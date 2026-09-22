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
        var result = await new DownloadEngine(client).RunAsync(new DownloadRequest
        {
            Uri = uri,
            Directory = staging,
            FileName = fileName,
            SortIntoCategories = false,
            Connections = 8,
        }, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(result.Path, Path.Combine(directory, fileName), overwrite: true);
    }
}
