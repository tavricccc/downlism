using Downlism.Core.Http;
using Xunit;

namespace Downlism.Tests;

public sealed class ContentRangeTests
{
    [Fact]
    public void ParsesProbeResponse()
    {
        Assert.True(ContentRange.TryParse("bytes 0-0/12345", out var range));
        Assert.Equal(0, range.Start);
        Assert.Equal(0, range.End);
        Assert.Equal(12345, range.TotalLength);
    }

    [Fact]
    public void ParsesUnknownTotalAsNull()
    {
        Assert.True(ContentRange.TryParse("bytes 0-0/*", out var range));
        Assert.Null(range.TotalLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items 0-0/5")]
    [InlineData("bytes 0-0")]
    [InlineData("bytes 5-0/10")]
    [InlineData("bytes -1-5/10")]
    [InlineData("bytes 0-0/abc")]
    public void RejectsMalformedValues(string? value)
    {
        Assert.False(ContentRange.TryParse(value, out _));
    }
}
