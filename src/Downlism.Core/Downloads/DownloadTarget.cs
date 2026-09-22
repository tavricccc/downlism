using Microsoft.Win32.SafeHandles;

namespace Downlism.Core.Downloads;

/// <summary>
/// The file being written, plus the rules for naming it and for publishing it once complete.
/// </summary>
/// <remarks>
/// Bytes land in a single <c>.download</c> file opened once and written concurrently at
/// distinct offsets. <see cref="SafeFileHandle"/> with <see cref="RandomAccess"/> supports
/// that directly; separate <see cref="FileStream"/> instances would each carry their own
/// buffer and position and fight over the same file.
/// </remarks>
public sealed class DownloadTarget : IDisposable
{
    public const string PartialExtension = ".download";

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private DownloadTarget(SafeFileHandle handle, string partialPath, string finalPath)
    {
        _handle = handle;
        PartialPath = partialPath;
        FinalPath = finalPath;
    }

    public string PartialPath { get; }

    public string FinalPath { get; }

    /// <summary>
    /// Creates or reopens the partial file. <paramref name="totalLength"/> is preallocated so
    /// the file system can lay the file out contiguously instead of extending it eight times
    /// a second from eight directions.
    /// </summary>
    public static DownloadTarget Open(string directory, string fileName, long? totalLength, bool resume)
    {
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, Http.SuggestedFileName.Sanitize(fileName));
        var partialPath = finalPath + PartialExtension;

        var mode = resume && File.Exists(partialPath) ? FileMode.Open : FileMode.Create;
        var handle = File.OpenHandle(partialPath, mode, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

        try
        {
            if (totalLength is >= 0 && RandomAccess.GetLength(handle) != totalLength)
                RandomAccess.SetLength(handle, totalLength.Value);
            return new DownloadTarget(handle, partialPath, finalPath);
        }
        catch { handle.Dispose(); throw; }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken);
    }

    public void Flush() => RandomAccess.FlushToDisk(_handle);

    /// <summary>
    /// Closes the handle, marks the file as internet-sourced, and renames it into place.
    /// Returns the path actually used, which differs from <see cref="FinalPath"/> when a
    /// file of that name already existed.
    /// </summary>
    public string Publish(Uri source)
    {
        if (!_disposed) Flush();
        Dispose();

        var destination = NextAvailablePath(FinalPath);
        File.Move(PartialPath, destination);
        WriteMarkOfTheWeb(destination, source);
        return destination;
    }

    /// <summary>
    /// Records the origin in the <c>Zone.Identifier</c> alternate data stream, the same way
    /// browsers do. Without it Windows treats a downloaded executable as locally authored and
    /// silently drops a protection the user expects to have.
    /// </summary>
    public static void WriteMarkOfTheWeb(string path, Uri source)
    {
        var content = $"""
            [ZoneTransfer]
            ZoneId=3
            HostUrl={source.AbsoluteUri}

            """;

        try
        {
            File.WriteAllText(path + ":Zone.Identifier", content);
        }
        catch (IOException)
        {
            // Alternate data streams need NTFS; a download onto FAT32 or exFAT still succeeded.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Appends " (2)", " (3)" and so on rather than overwriting an existing file.</summary>
    public static string NextAvailablePath(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var suffix = 2; suffix < 10000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}
