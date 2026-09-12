using Downlism.Core.Downloads;
using Xunit;

namespace Downlism.Tests;

public sealed class SegmentStateFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("downlism-tests").FullName;

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void SurvivesReopenWithProgressIntact()
    {
        var path = PathFor("a.bin.dlstate");
        var planned = SegmentPlanner.Plan(1000, 4, minimumSegmentLength: 1);

        using (var state = SegmentStateFile.Create(path, 1000, "\"etag-1\"", planned))
        {
            state.Update(0, 100);
            state.Update(2, 50);
            state.Flush();
        }

        using var reopened = SegmentStateFile.TryOpen(path);
        Assert.NotNull(reopened);
        Assert.Equal(1000, reopened.TotalLength);
        Assert.Equal("\"etag-1\"", reopened.Validator);
        Assert.Equal(4, reopened.Segments.Length);
        Assert.Equal(100, reopened.Segments[0].Completed);
        Assert.Equal(50, reopened.Segments[2].Completed);
        Assert.Equal(150, reopened.CompletedBytes());
        Assert.False(reopened.IsComplete());
    }

    [Fact]
    public void ReportsCompletionWhenEverySegmentIsFull()
    {
        var path = PathFor("b.bin.dlstate");
        var planned = SegmentPlanner.Plan(400, 4, minimumSegmentLength: 1);

        using var state = SegmentStateFile.Create(path, 400, string.Empty, planned);
        for (var index = 0; index < state.Segments.Length; index++)
        {
            state.Update(index, state.Segments[index].Length);
        }

        Assert.True(state.IsComplete());
        Assert.Equal(400, state.CompletedBytes());
    }

    [Fact]
    public void KeepsLongValidatorWithinCapacity()
    {
        var path = PathFor("c.bin.dlstate");
        var validator = new string('x', 400);

        using (SegmentStateFile.Create(path, 100, validator, SegmentPlanner.Plan(100, 1)))
        {
        }

        using var reopened = SegmentStateFile.TryOpen(path);
        Assert.NotNull(reopened);
        Assert.Equal(256, reopened.Validator.Length);
    }

    [Fact]
    public void TreatsMissingSidecarAsFreshStart()
    {
        Assert.Null(SegmentStateFile.TryOpen(PathFor("absent.dlstate")));
    }

    [Fact]
    public void RejectsForeignFileInsteadOfMisreadingIt()
    {
        var path = PathFor("d.bin.dlstate");
        File.WriteAllText(path, "this is not a state file, but it is long enough to fill a header 0123456789");

        Assert.Null(SegmentStateFile.TryOpen(path));
    }

    [Fact]
    public void RejectsTruncatedFile()
    {
        var path = PathFor("e.bin.dlstate");
        using (SegmentStateFile.Create(path, 1000, "\"etag\"", SegmentPlanner.Plan(1000, 4, minimumSegmentLength: 1)))
        {
        }

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length - 8);
        }

        Assert.Null(SegmentStateFile.TryOpen(path));
    }

    [Fact]
    public void SidecarPathSitsBesideTheTargetFile()
    {
        Assert.Equal(@"C:\downloads\a.zip.dlstate", SegmentStateFile.PathFor(@"C:\downloads\a.zip"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
