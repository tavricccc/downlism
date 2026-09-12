using Downlism.Core.Http;
using Xunit;

namespace Downlism.Tests;

public sealed class SuggestedFileNameTests
{
    [Fact]
    public void PrefersExtendedFileNameOverPlain()
    {
        var header = "attachment; filename=\"fallback.txt\"; filename*=UTF-8''%E4%B8%AD%E6%96%87.txt";
        Assert.Equal("中文.txt", SuggestedFileName.FromContentDisposition(header));
    }

    [Fact]
    public void UsesPlainFileNameWhenNoExtendedForm()
    {
        Assert.Equal("report final.pdf", SuggestedFileName.FromContentDisposition("attachment; filename=\"report final.pdf\""));
    }

    [Fact]
    public void IgnoresSemicolonInsideQuotedValue()
    {
        Assert.Equal("a;b.txt", SuggestedFileName.FromContentDisposition("attachment; filename=\"a;b.txt\""));
    }

    [Fact]
    public void ReturnsNullWhenNoFileNameParameter()
    {
        Assert.Null(SuggestedFileName.FromContentDisposition("inline"));
        Assert.Null(SuggestedFileName.FromContentDisposition(null));
    }

    [Theory]
    [InlineData("../../autorun.inf", "autorun.inf")]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "hosts")]
    [InlineData("evil.exe. ", "evil.exe")]
    [InlineData("a<b>c|d.txt", "a_b_c_d.txt")]
    [InlineData("..", "download")]
    [InlineData("   ", "download")]
    public void SanitizesHostileNames(string input, string expected)
    {
        Assert.Equal(expected, SuggestedFileName.Sanitize(input));
    }

    [Fact]
    public void FallsBackToFinalUriPathSegment()
    {
        Assert.Equal("setup.exe", SuggestedFileName.FromUri(new Uri("https://example.com/a/b/setup.exe?token=1")));
    }

    [Fact]
    public void UnescapesPercentEncodedUriSegment()
    {
        Assert.Equal("中文.zip", SuggestedFileName.FromUri(new Uri("https://example.com/%E4%B8%AD%E6%96%87.zip")));
    }

    [Fact]
    public void FallsBackWhenUriHasNoFileSegment()
    {
        Assert.Equal("download", SuggestedFileName.FromUri(new Uri("https://example.com/")));
    }
}
