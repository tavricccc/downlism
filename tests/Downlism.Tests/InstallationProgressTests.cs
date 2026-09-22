using System.Security.Cryptography;
using System.Text.Json;
using Downlism.Core.Installation;
using Xunit;

namespace Downlism.Tests;

public sealed class InstallationProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Downlism-install-progress-" + Guid.NewGuid());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReportsActualWorkForInstallAndRemoval(bool keepData)
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(source);
        var files = new Dictionary<string, string>();
        foreach (var name in new[] { "Downlism.App.exe", "Downlism.Setup.exe", "Uninstall.exe", "Downlism.Host.exe", "coreclr.dll", "Microsoft.WinUI.dll" })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            File.WriteAllBytes(Path.Combine(source, name), bytes);
            files[name] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        File.WriteAllText(Path.Combine(source, InstallFiles.ManifestName),
            JsonSerializer.Serialize(new InstallManifest("Downlism", "0.8.1", files)));
        var samples = new List<InstallationProgress>();
        var report = new Reporter(samples.Add);
        var registered = false;
        InstallationUpdate.Apply(source, target, _ => registered = true, report);
        Assert.True(registered);
        foreach (var phase in samples.GroupBy(sample => sample.Phase))
        {
            Assert.Equal(0, phase.First().Completed);
            Assert.Equal(1, phase.Last().Fraction);
            Assert.Equal(Enumerable.Range(0, phase.Last().Total + 1), phase.Select(sample => sample.Completed));
        }
        Assert.Equal(6, samples.First().Total);
        samples.Clear();
        InstallFiles.RemoveInstallation(target, keepData, report);
        Assert.Equal(6, samples.Last().Completed);
        Assert.Equal(1, samples.Last().Fraction);
        Assert.False(Directory.Exists(target));
    }

    private sealed class Reporter(Action<InstallationProgress> report) : IProgress<InstallationProgress>
    { public void Report(InstallationProgress sample) => report(sample); }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
