using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Downlism.Core.Downloads;

/// <summary>
/// Per-segment progress, stored in a fixed-layout sidecar next to the target file.
/// </summary>
/// <remarks>
/// Deliberately not in SQLite. Eight connections report progress tens of times a second;
/// that write rate bloats the WAL and stalls checkpoints on the UI thread. Crash recovery
/// only needs local truth — how far this one file got — not global transactional
/// consistency, and a fixed layout lets each segment be rewritten in place at a known
/// offset with no serialization.
/// </remarks>
public sealed class SegmentStateFile : IDisposable
{
    private const uint Magic = 0x5453_4C44; // "DLST" little-endian
    private const int Version = 1;
    private const int ValidatorCapacity = 256;
    private const int HeaderLength = 4 + 4 + 8 + 4 + 4 + ValidatorCapacity;
    private const int SegmentRecordLength = 8 + 8 + 8;

    private readonly SafeFileHandle _handle;
    private readonly byte[] _record = new byte[SegmentRecordLength];
    private bool _disposed;

    private SegmentStateFile(SafeFileHandle handle, long totalLength, string validator, Segment[] segments)
    {
        _handle = handle;
        TotalLength = totalLength;
        Validator = validator;
        Segments = segments;
    }

    public long TotalLength { get; }

    /// <summary>The <c>ETag</c> or <c>Last-Modified</c> captured when the download started.</summary>
    public string Validator { get; }

    public Segment[] Segments { get; }

    public static string PathFor(string targetPath) => targetPath + ".dlstate";

    public static SegmentStateFile Create(string path, long totalLength, string validator, IReadOnlyList<Segment> segments)
    {
        var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var buffer = new byte[HeaderLength + segments.Count * SegmentRecordLength];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Version);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), segments.Count);

        var validatorBytes = Encoding.UTF8.GetBytes(validator);
        if (validatorBytes.Length > ValidatorCapacity) validatorBytes = validatorBytes[..ValidatorCapacity];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), validatorBytes.Length);
        validatorBytes.CopyTo(buffer.AsSpan(24));

        var copy = new Segment[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            copy[index] = segments[index];
            WriteRecord(buffer.AsSpan(HeaderLength + index * SegmentRecordLength), segments[index]);
        }

        RandomAccess.Write(handle, buffer, 0);
        return new SegmentStateFile(handle, totalLength, Encoding.UTF8.GetString(validatorBytes), copy);
    }

    /// <summary>
    /// Reopens an interrupted download. Returns null when the sidecar is missing, truncated,
    /// or written by a layout this build does not understand — all of which mean "start over"
    /// rather than "fail", because a corrupt sidecar must never corrupt the target file.
    /// </summary>
    public static SegmentStateFile? TryOpen(string path)
    {
        if (!File.Exists(path)) return null;

        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            var header = new byte[HeaderLength];
            if (RandomAccess.Read(handle, header, 0) != HeaderLength) return Reject(handle);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) return Reject(handle);
            if (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != Version) return Reject(handle);

            var totalLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            if (count is <= 0 or > 1024) return Reject(handle);

            var validatorLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
            if (validatorLength is < 0 or > ValidatorCapacity) return Reject(handle);
            var validator = Encoding.UTF8.GetString(header, 24, validatorLength);

            var body = new byte[count * SegmentRecordLength];
            if (RandomAccess.Read(handle, body, HeaderLength) != body.Length) return Reject(handle);

            var segments = new Segment[count];
            for (var index = 0; index < count; index++)
            {
                var span = body.AsSpan(index * SegmentRecordLength);
                var start = BinaryPrimitives.ReadInt64LittleEndian(span);
                var end = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
                var completed = BinaryPrimitives.ReadInt64LittleEndian(span[16..]);
                if (start < 0 || end < start || completed < 0 || completed > end - start + 1) return Reject(handle);
                segments[index] = new Segment(start, end, completed);
            }

            var opened = new SegmentStateFile(handle, totalLength, validator, segments);
            handle = null;
            return opened;
        }
        catch (IOException)
        {
            return Reject(handle);
        }
        catch (UnauthorizedAccessException)
        {
            return Reject(handle);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>Rewrites one segment record in place. Called from the segment's own worker.</summary>
    public void Update(int index, long completed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Segments.Length);

        Segments[index] = Segments[index] with { Completed = completed };
        WriteRecord(_record, Segments[index]);
        RandomAccess.Write(_handle, _record, HeaderLength + (long)index * SegmentRecordLength);
    }

    public long CompletedBytes()
    {
        var total = 0L;
        foreach (var segment in Segments) total += segment.Completed;
        return total;
    }

    public bool IsComplete()
    {
        foreach (var segment in Segments)
        {
            if (!segment.IsComplete) return false;
        }

        return true;
    }

    public void Flush() => RandomAccess.FlushToDisk(_handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    private static SegmentStateFile? Reject(SafeFileHandle? handle)
    {
        handle?.Dispose();
        return null;
    }

    private static void WriteRecord(Span<byte> destination, Segment segment)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, segment.Start);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], segment.End);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], segment.Completed);
    }
}
