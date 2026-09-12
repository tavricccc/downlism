namespace Downlism.Core.Downloads;

/// <summary>Splits a known content length into the ranges each connection will fetch.</summary>
public static class SegmentPlanner
{
    /// <summary>
    /// Below this size extra connections cost more in round trips than they save, and many
    /// servers throttle per-connection setup harder than per-byte transfer.
    /// </summary>
    public const long DefaultMinimumSegmentLength = 1024 * 1024;

    public static IReadOnlyList<Segment> Plan(
        long totalLength,
        int maximumConnections,
        long minimumSegmentLength = DefaultMinimumSegmentLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSegmentLength);

        // Never create a segment smaller than the floor, but always create at least one.
        var affordable = (int)Math.Min(maximumConnections, Math.Max(1, totalLength / minimumSegmentLength));
        var count = Math.Max(1, affordable);

        var segments = new Segment[count];
        var baseLength = totalLength / count;
        var remainder = totalLength % count;

        var start = 0L;
        for (var index = 0; index < count; index++)
        {
            // Spread the remainder over the leading segments instead of loading it all onto
            // the last one, which would leave one connection running long after the others.
            var length = baseLength + (index < remainder ? 1 : 0);
            segments[index] = new Segment(start, start + length - 1, 0);
            start += length;
        }

        return segments;
    }

    /// <summary>A single segment covering an unknown-length body, downloaded on one connection.</summary>
    public static IReadOnlyList<Segment> PlanUnknownLength() => [new Segment(0, long.MaxValue - 1, 0)];
}
