namespace Downlism.Core.Settings;

/// <summary>An explicit profile directory keeps development/smoke tests away from real history.</summary>
public static class AppDataPaths
{
    public static string Root
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DOWNLISM_DATA_DIRECTORY");
            if (string.IsNullOrWhiteSpace(custom)) return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downlism");
            if (!Path.IsPathFullyQualified(custom)) throw new InvalidOperationException("DOWNLISM_DATA_DIRECTORY must be an absolute path.");
            return Path.GetFullPath(custom);
        }
    }
}
