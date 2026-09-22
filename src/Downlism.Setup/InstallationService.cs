using System.Diagnostics;
using System.Runtime.InteropServices;
using Downlism.Core.Installation;
using Microsoft.Win32;

namespace Downlism.Setup;

public sealed class InstallationService
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Downlism";
    public static string? InstalledPath
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return InstallationDiscovery.FindExisting(key?.GetValue("InstallLocation") as string, DefaultPath);
        }
    }
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Downlism");
    private static string StartShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Downlism.lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Downlism.lnk");

    public static string ValidateTarget(string target)
    {
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(target) == target ||
            !Path.GetFileName(target).Equals("Downlism", StringComparison.OrdinalIgnoreCase))
            throw new IOException("請選擇本機磁碟中名為 Downlism 的專用資料夾。");
        InstallFiles.RejectReparsePoints(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            if (!string.Equals(target, InstalledPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("請選擇空資料夾，或更新目前登錄的安裝位置。");
            InstallFiles.ReadManifest(target);
        }
        if (File.Exists(target)) throw new IOException("安裝位置已被檔案佔用。");
        return target;
    }

    public static bool HasDesktopShortcut
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("DesktopShortcut") is int value) return value == 1;
            if (!File.Exists(DesktopLink) || InstalledPath is not { } target) return false;
            var matches = false;
            WithShortcut(DesktopLink, shortcut => matches = string.Equals((string)shortcut.TargetPath,
                Path.Combine(target, "Downlism.App.exe"), StringComparison.OrdinalIgnoreCase));
            return matches;
        }
    }

    public static void Install(string target, bool desktop, IProgress<InstallationProgress>? progress = null)
    {
        target = ValidateTarget(target);
        var updating = InstalledPath is not null;
        if (updating && !string.Equals(target, InstalledPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新必須使用目前安裝位置。");
        if (!updating && (File.Exists(StartShortcut) || (desktop && File.Exists(DesktopLink))))
            throw new IOException("已存在 Downlism 捷徑，請先移除或重新命名該捷徑。");
        InstalledAppShutdown.Stop(target);
        var oldStart = File.Exists(StartShortcut) ? File.ReadAllBytes(StartShortcut) : null;
        var oldDesktop = File.Exists(DesktopLink) ? File.ReadAllBytes(DesktopLink) : null;
        using var existing = Registry.CurrentUser.OpenSubKey(UninstallKey);
        var oldValues = existing?.GetValueNames().ToDictionary(name => name,
            name => (Value: existing.GetValue(name)!, Kind: existing.GetValueKind(name)));
        existing?.Close();
        try
        {
            InstallationUpdate.Apply(AppContext.BaseDirectory, target, manifest =>
            {
                CreateShortcut(StartShortcut, target);
                if (desktop) CreateShortcut(DesktopLink, target);
                var setupExe = Path.Combine(target, "Downlism.Setup.exe");
                var uninstallExe = Path.Combine(target, "Uninstall.exe");
                if (File.Exists(setupExe))
                {
                    try { File.Copy(setupExe, uninstallExe, true); } catch { }
                }
                using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
                key.SetValue("DisplayName", "Downlism");
                key.SetValue("DisplayVersion", manifest.Version);
                key.SetValue("InstallLocation", target);
                key.SetValue("DisplayIcon", Path.Combine(target, "Downlism.App.exe"));
                key.SetValue("UninstallString", MaintenanceLauncher.CreateCommand(target));
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("DesktopShortcut", desktop ? 1 : 0, RegistryValueKind.DWord);
                // Point Chrome and Edge at the host that ships with this installation, so the
                // browser extension can hand downloads over as soon as it is loaded.
                BrowserRegistration.Register(target);
            }, progress);
        }
        catch
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (oldValues is not null)
            {
                using var restored = Registry.CurrentUser.CreateSubKey(UninstallKey);
                foreach (var (name, entry) in oldValues) restored.SetValue(name, entry.Value, entry.Kind);
            }
            if (oldStart is null) File.Delete(StartShortcut); else File.WriteAllBytes(StartShortcut, oldStart);
            if (oldDesktop is null) File.Delete(DesktopLink); else File.WriteAllBytes(DesktopLink, oldDesktop);
            throw;
        }
        // Older releases cached the entire packed installer. It is no longer needed.
        var legacyCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downlism", "Installer");
        try
        {
            if (Directory.Exists(legacyCache))
            {
                InstallFiles.RejectReparseTree(legacyCache);
                Directory.Delete(legacyCache, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Uninstall(bool keepData, IProgress<InstallationProgress>? progress = null)
    {
        var target = InstalledPath ?? throw new IOException("找不到 Downlism 安裝紀錄。");
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Path.GetFileName(target).Equals("Downlism", StringComparison.OrdinalIgnoreCase))
            throw new IOException("安裝位置不符，已停止解除安裝。");
        InstallFiles.ReadManifest(target);
        InstalledAppShutdown.Stop(target);
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (run?.GetValue("Downlism") is string command &&
                command.StartsWith($"\"{Path.Combine(target, "Downlism.App.exe")}\"", StringComparison.OrdinalIgnoreCase))
                run.DeleteValue("Downlism", false);
        }
        // Remove the browser registrations before the files, so no browser is left pointing at
        // a host manifest that no longer exists.
        BrowserRegistration.Unregister();
        InstallFiles.RemoveInstallation(target, keepData, progress);
        RemoveMatchingShortcut(StartShortcut, target);
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            if (key?.GetValue("DesktopShortcut") is int value && value == 1) RemoveMatchingShortcut(DesktopLink, target);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "uninstall-complete"), keepData ? "keep" : "remove");
    }

    private static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WithShortcut(path, shortcut =>
        {
            shortcut.TargetPath = Path.Combine(target, "Downlism.App.exe");
            shortcut.IconLocation = Path.Combine(target, "Assets", "Downlism.ico") + ",0";
            shortcut.WorkingDirectory = target;
            shortcut.Description = "搜尋應用程式與檔案";
            shortcut.Save();
        });
    }

    private static void RemoveMatchingShortcut(string path, string target)
    {
        if (!File.Exists(path)) return;
        var matches = false;
        WithShortcut(path, shortcut => matches = string.Equals((string)shortcut.TargetPath,
            Path.Combine(target, "Downlism.App.exe"), StringComparison.OrdinalIgnoreCase));
        if (matches) File.Delete(path);
    }

    private static void WithShortcut(string path, Action<dynamic> action)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try { shortcut = ((dynamic)shell).CreateShortcut(path); action(shortcut); }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
