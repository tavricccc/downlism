using System.Text.Json;
using System.Text.Json.Serialization;

namespace Downlism.Core.Settings;

/// <summary>What the user has chosen, kept in one small file next to the download database.</summary>
public sealed record AppSettings
{
    /// <summary>Sort finished files into folders by type instead of one flat Downloads folder.</summary>
    public bool SortIntoCategories { get; init; } = true;

    /// <summary>Offer to take a download link the moment it is copied.</summary>
    public bool WatchClipboard { get; init; }

    public int Connections { get; init; } = 8;

    public long BytesPerSecond { get; init; }

    public int ConcurrentDownloads { get; init; } = 3;

    public int RetryAttempts { get; init; } = 3;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Downlism",
        "settings.json");

    /// <summary>
    /// Reads the file, falling back to defaults for anything missing or unreadable. A corrupt
    /// settings file must never stop the app starting: the worst case is that a preference is
    /// forgotten, which the user can see and fix.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings)
                ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written through a temporary file: a half-written settings file would be parsed as
        // corrupt on the next start and silently reset every preference.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
        File.Move(temporary, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
