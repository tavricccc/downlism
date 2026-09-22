using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Downlism.Core.Downloads;

namespace Downlism.Core.Settings;

public sealed record AppSettings
{
    public bool SortIntoCategories { get; init; } = true;
    public bool WatchClipboard { get; init; }
    public bool PromptOnCapture { get; init; } = true;
    public string? DownloadFolder { get; init; }
    public int Connections { get; init; } = 8;
    public long BytesPerSecond { get; init; }
    public int ConcurrentDownloads { get; init; } = 3;
    public int RetryAttempts { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 5;
    public int ReadTimeoutSeconds { get; init; } = 60;
    public string Theme { get; init; } = "System";
    public bool CloseToTray { get; init; } = true;
    public bool NotifyOnComplete { get; init; }
    public bool NotifyOnFailure { get; init; } = true;
    public bool ResumeOnStartup { get; init; }
    public bool KeepProgressWindow { get; init; } = true;
    public bool PromptAlwaysOnTop { get; init; }
    public bool PreventDuplicateDownloads { get; init; } = true;
    public string CategoryRules { get; init; } = "";

    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "settings.json");

    public AppSettings Normalize() => this with
    {
        Connections = Math.Clamp(Connections, 1, 32),
        ConcurrentDownloads = Math.Clamp(ConcurrentDownloads, 1, 16),
        BytesPerSecond = Math.Clamp(BytesPerSecond, 0, 10L * 1024 * 1024 * 1024),
        RetryAttempts = Math.Clamp(RetryAttempts, 1, 10),
        RetryDelaySeconds = Math.Clamp(RetryDelaySeconds, 1, 120),
        ReadTimeoutSeconds = Math.Clamp(ReadTimeoutSeconds, 5, 600),
        Theme = Theme is "Light" or "Dark" ? Theme : "System",
        DownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? null : DownloadFolder.Trim(),
        CategoryRules = CategoryRules ?? "",
    };

    public void Validate()
    {
        if (Connections is < 1 or > 32 || ConcurrentDownloads is < 1 or > 16 ||
            RetryAttempts is < 1 or > 10 || RetryDelaySeconds is < 1 or > 120 ||
            ReadTimeoutSeconds is < 5 or > 600 || BytesPerSecond is < 0 or > 10L * 1024 * 1024 * 1024)
            throw new ArgumentException("數值超出允許範圍，請依欄位標示調整。");
        if (Theme is not ("System" or "Light" or "Dark")) throw new ArgumentException("無效的佈景主題。");
        if (!string.IsNullOrWhiteSpace(DownloadFolder) &&
            (!Path.IsPathFullyQualified(DownloadFolder) || DownloadFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
            throw new ArgumentException("下載資料夾必須是完整路徑，例如 C:\\Downloads。");
        CategoryRule.Parse(CategoryRules);
    }

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new();
            var settings = FromJson(File.ReadAllText(path)).Normalize();
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return new();
        }
    }

    public static AppSettings FromJson(string json)
    {
        if (json.Length > 65_536) throw new JsonException("設定檔過大。");
        if (JsonNode.Parse(json) is not JsonObject supplied) throw new JsonException("設定檔必須是 JSON 物件。");
        // Source-generated init-only deserialization supplies zero for absent constructor
        // slots. Merge defaults first so adding a preference never resets older installations.
        var merged = (JsonObject)JsonNode.Parse(new AppSettings().ToJson())!;
        foreach (var pair in supplied) merged[pair.Key] = pair.Value?.DeepClone();
        return JsonSerializer.Deserialize(merged.ToJsonString(), SettingsJsonContext.Default.AppSettings)
            ?? throw new JsonException("設定檔內容為空。");
    }

    public string ToJson() => JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings);

    public void Save(string? path = null)
    {
        Validate();
        path = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, ToJson());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
