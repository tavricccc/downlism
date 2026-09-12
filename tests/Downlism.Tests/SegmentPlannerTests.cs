using Downlism.Core.Downloads;
using Xunit;

namespace Downlism.Tests;

public sealed class SegmentPlannerTests
{
    [Fact]
    public void SegmentsCoverTheWholeFileWithoutOverlap()
    {
        var segments = SegmentPlanner.Plan(totalLength: 1000, maximumConnections: 8, minimumSegmentLength: 1);

        Assert.Equal(8, segments.Count);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(999, segments[^1].End);
        Assert.Equal(1000, segments.Sum(segment => segment.Length));

        for (var index = 1; index < segments.Count; index++)
        {
            Assert.Equal(segments[index - 1].End + 1, segments[index].Start);
        }
    }

    [Fact]
    public void SpreadsRemainderAcrossLeadingSegments()
    {
        var segments = SegmentPlanner.Plan(totalLength: 10, maximumConnections: 3, minimumSegmentLength: 1);

        Assert.Equal([4L, 3L, 3L], segments.Select(segment => segment.Length));
    }

    [Fact]
    public void DoesNotSplitBelowTheMinimumSegmentLength()
    {
        var segments = SegmentPlanner.Plan(totalLength: 3 * 1024 * 1024, maximumConnections: 16);

        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void AlwaysProducesOneSegmentForTinyFiles()
    {
        var segments = SegmentPlanner.Plan(totalLength: 10, maximumConnections: 8);

        Assert.Single(segments);
        Assert.Equal(10, segments[0].Length);
    }

    [Fact]
    public void RangeHeaderResumesFromCompletedOffset()
    {
        var segment = new Segment(100, 199, 40);

        Assert.Equal("bytes=140-199", segment.ToRangeHeaderValue());
        Assert.Equal(60, segment.Remaining);
        Assert.False(segment.IsComplete);
    }
}
