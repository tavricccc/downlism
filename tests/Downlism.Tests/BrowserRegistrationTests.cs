using System.Text.Json;
using Downlism.Core.Ingest;
using Downlism.Core.Installation;
using Xunit;

namespace Downlism.Tests;

public sealed class BrowserRegistrationTests
{
    [Fact]
    public void ManifestNamesTheHostTheExtensionAsksFor()
    {
        using var document = JsonDocument.Parse(BrowserRegistration.BuildManifest(@"C:\Apps\Downlism\Downlism.Host.exe"));
        var root = document.RootElement;

        Assert.Equal("com.downlism.host", root.GetProperty("name").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal(@"C:\Apps\Downlism\Downlism.Host.exe", root.GetProperty("path").GetString());
    }

    [Fact]
    public void ManifestAllowsOnlyOurOwnExtension()
    {
        using var document = JsonDocument.Parse(BrowserRegistration.BuildManifest(@"C:\Apps\Downlism.Host.exe"));
        var origins = document.RootElement.GetProperty("allowed_origins").EnumerateArray()
            .Select(origin => origin.GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal([$"chrome-extension://{BrowserRegistration.ExtensionId}/"], origins);
    }

    [Fact]
    public void ExtensionIdMatchesTheKeyEmbeddedInTheShippedManifest()
    {
        // The ID is derived from the public key in extension/manifest.json. If the key is ever
        // regenerated without updating this constant, every browser registration silently stops
        // matching and downloads quietly stop being handed over.
        var manifestPath = Path.Combine(RepositoryRoot(), "extension", "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var key = document.RootElement.GetProperty("key").GetString();

        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.Equal(BrowserRegistration.ExtensionId, DeriveExtensionId(key!));
    }

    /// <summary>
    /// Reproduces Chrome's derivation: the first sixteen bytes of the SHA-256 of the DER public
    /// key, with each nibble rendered as a letter from 'a' to 'p'.
    /// </summary>
    private static string DeriveExtensionId(string base64PublicKey)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(base64PublicKey));
        return string.Concat(digest.Take(16).SelectMany(value => new[]
        {
            (char)('a' + (value >> 4)),
            (char)('a' + (value & 0x0F)),
        }));
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Downlism.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new DirectoryNotFoundException("Cannot locate the repository root.");
    }
}

public sealed class IngestMessageTests
{
    [Theory]
    [InlineData("https://example.com/a.zip")]
    [InlineData("http://example.com/a.zip")]
    public void AcceptsWebDownloads(string url)
    {
        Assert.True(new IngestMessage { Url = url }.TryGetUri(out _));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/a.zip")]
    [InlineData("not a url")]
    [InlineData("")]
    public void RejectsAnythingThatIsNotHttp(string url)
    {
        // The channel is reachable from any page the extension runs on, so a scheme that reads
        // local files or executes script must never survive the handover.
        Assert.False(new IngestMessage { Url = url }.TryGetUri(out _));
    }
}
