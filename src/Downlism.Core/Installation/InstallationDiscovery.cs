using System.Security.Cryptography;
using System.Text.Json;

namespace Downlism.Core.Installation;

public static class InstallationDiscovery
{
    public static string? FindExisting(string? registeredPath, string defaultPath)
    {
        if (!string.IsNullOrWhiteSpace(registeredPath)) return registeredPath;
        // Recover only the known default location. An arbitrary folder named Downlism
        // is not enough: it must carry a valid ownership manifest and the matching app.
        try
        {
            if (!File.Exists(Path.Combine(defaultPath, InstallFiles.ManifestName))) return null;
            var manifest = InstallFiles.ReadManifest(defaultPath);
            if (!Version.TryParse(manifest.Version, out _) || !manifest.Files.TryGetValue("Downlism.App.exe", out var hash)
                || !manifest.Files.ContainsKey("Downlism.Setup.exe")) return null;
            using var executable = new FileStream(InstallFiles.Resolve(defaultPath, "Downlism.App.exe"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(executable)).Equals(hash, StringComparison.OrdinalIgnoreCase)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultPath)) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
