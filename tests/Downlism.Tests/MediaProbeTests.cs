using Downlism.Core.Downloads;
using Downlism.Core.Media;
using Xunit;

namespace Downlism.Tests;

public sealed class MediaProbeTests
{
    [Fact]
    public void ProbeDoesNotRequireADownloadableDefaultFormat()
    {
        using var client = new HttpClient();
        var tools = new MediaTools(client);
        var probe = new MediaProbe(tools);
        var request = new DownloadRequest
        {
            Uri = new Uri("https://www.youtube.com/watch?v=LYANS5AwwnA&list=radio"),
            Directory = Path.GetTempPath(),
            MediaQuality = 2160,
        };
        var arguments = probe.BuildStartInfo(request).ArgumentList;
        Assert.Contains("--ignore-config", arguments);
        Assert.Contains("--ignore-no-formats-error", arguments);
        Assert.Contains("--no-playlist", arguments);
        Assert.Contains("--skip-download", arguments);
        Assert.Contains("deno:" + tools.DenoPath, arguments);
        Assert.DoesNotContain("--format", arguments);
        Assert.Equal(request.Uri.AbsoluteUri, arguments.Last());
    }
}
