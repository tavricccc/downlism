using Microsoft.Win32;

namespace Downlism.App.Services;

/// <summary>
/// Starts Downlism with Windows, per user, through the Run key.
/// </summary>
/// <remarks>
/// Matters more here than for a launcher: the browser extension hands downloads to whatever is
/// listening, and a Downlism that is not running means the first download after every reboot
/// is silently handled by the browser instead. Started with <c>--background</c> so it appears
/// in the tray without a window stealing focus during sign-in.
/// </remarks>
public sealed class LoginStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Downlism";

    public const string BackgroundArgument = "--background";

    /// <summary>
    /// Passed by the installer on the launch that follows a fresh install, so the app knows to
    /// show the extension guide once. Kept here beside the other startup switch rather than
    /// invented in two places that would drift apart.
    /// </summary>
    public const string ExtensionGuideArgument = "--extension-guide";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("無法開啟目前使用者的登入啟動設定。");

        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("無法取得程式路徑。");
            key.SetValue(ValueName, $"\"{executable}\" {BackgroundArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static bool StartedInBackground() => HasArgument(BackgroundArgument);

    /// <summary>True on the first launch after an install, which is the moment the browser
    /// extension still has to be loaded by hand.</summary>
    public static bool StartedForExtensionGuide() => HasArgument(ExtensionGuideArgument);

    private static bool HasArgument(string argument) =>
        Environment.GetCommandLineArgs().Contains(argument, StringComparer.OrdinalIgnoreCase);
}
