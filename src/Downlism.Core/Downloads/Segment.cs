namespace Downlism.Core.Downloads;

/// <summary>
/// One contiguous byte range of the target file. <see cref="Start"/> and <see cref="End"/>
/// are inclusive, matching HTTP <c>Range</c> semantics so the values can be used verbatim.
/// </summary>
public readonly record struct Segment(long Start, long End, long Completed)
{
    public long Length => End - Start + 1;

    public long Remaining => Length - Completed;

    public bool IsComplete => Completed >= Length;

    /// <summary>Where the next byte for this segment lands in the target file.</summary>
    public long NextOffset => Start + Completed;

    /// <summary>The <c>Range</c> header value for the unfinished remainder.</summary>
    public string ToRangeHeaderValue() => $"bytes={NextOffset}-{End}";
}
