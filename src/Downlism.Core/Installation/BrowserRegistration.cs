using System.Text.Json;
using Microsoft.Win32;

namespace Downlism.Core.Installation;

/// <summary>
/// Registers the native messaging host so Chrome and Edge will launch it.
/// </summary>
/// <remarks>
/// Registration is per-user under HKCU, which keeps the installer free of an elevation prompt.
/// The extension is named by a fixed ID, which only works because the extension embeds its own
/// public key; an unpacked extension otherwise takes its ID from the folder it was loaded from
/// and would need a different manifest on every machine.
/// </remarks>
public static class BrowserRegistration
{
    public const string HostName = "com.downlism.host";
    public const string ExtensionId = "fcnaeaaphjcgmiojojkjehnealidjodm";
    public const string ManifestFileName = "downlism-host.json";

    /// <summary>The registry paths each Chromium browser reads host registrations from.</summary>
    private static readonly string[] BrowserKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\",
        @"Software\Microsoft\Edge\NativeMessagingHosts\",
        @"Software\Chromium\NativeMessagingHosts\",
    ];

    /// <summary>
    /// Writes the host manifest into the installation folder and points the browsers at it.
    /// </summary>
    /// <param name="installationDirectory">Where Downlism.Host.exe lives.</param>
    public static void Register(string installationDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
        var host = Path.Combine(root, "Downlism.Host.exe");
        var manifestPath = Path.Combine(root, ManifestFileName);

        File.WriteAllText(manifestPath, BuildManifest(host));

        foreach (var key in BrowserKeys)
        {
            using var registryKey = Registry.CurrentUser.CreateSubKey(key + HostName);
            registryKey.SetValue(string.Empty, manifestPath);
        }
    }

    public static void Unregister()
    {
        foreach (var key in BrowserKeys)
        {
            // Delete only our own leaf key; the parent holds other products' registrations.
            Registry.CurrentUser.DeleteSubKeyTree(key + HostName, throwOnMissingSubKey: false);
        }
    }

    /// <summary>Builds the manifest content. Separated so its shape can be asserted in tests.</summary>
    public static string BuildManifest(string hostExecutablePath) => JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "Downlism download handover",
            ["path"] = hostExecutablePath,
            ["type"] = "stdio",
            ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
        },
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>True when at least one browser currently points at this installation.</summary>
    public static bool IsRegistered(string installationDirectory)
    {
        var expected = Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory)),
            ManifestFileName);

        foreach (var key in BrowserKeys)
        {
            using var registryKey = Registry.CurrentUser.OpenSubKey(key + HostName);
            if (registryKey?.GetValue(string.Empty) is string path
                && string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
