using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Downlism.Core.Installation;

public sealed record InstallManifest(string Product, string Version, Dictionary<string, string> Files);

/// <summary>File ownership and staging, independent of Windows registration and UI.</summary>
public static class InstallFiles
{
    public const string ManifestName = "downlism-install.json";

    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("安裝檔案路徑不合法。");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安裝檔案超出目標資料夾。");
        RejectReparsePoints(result);
        return result;
    }

    public static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("安裝位置不能包含符號連結或接合點。");
    }

    public static InstallManifest ReadManifest(string root)
    {
        RejectReparsePoints(root);
        var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(Resolve(root, ManifestName)))
            ?? throw new InvalidDataException("缺少安裝紀錄。");
        if (manifest.Product != "Downlism" || manifest.Files is null || manifest.Files.Count == 0)
            throw new InvalidDataException("不是 Downlism 管理的安裝資料夾。");
        manifest = manifest with { Files = new Dictionary<string, string>(manifest.Files, StringComparer.OrdinalIgnoreCase) };
        foreach (var file in manifest.Files.Keys) _ = Resolve(root, file);
        return manifest;
    }

    public static void RejectReparseTree(string root)
    {
        RejectReparsePoints(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("清理資料夾包含符號連結或接合點。");
            if (attributes.HasFlag(FileAttributes.Directory)) RejectReparseTree(entry);
        }
    }

    public static InstallManifest ExtractVerified(string archive, string staging, string expectedArchiveHash)
    {
        RejectReparsePoints(staging);
        if (Directory.Exists(staging)) throw new IOException("暫存安裝資料夾已存在。");
        using var stream = File.OpenRead(archive);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedArchiveHash.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安裝包校驗失敗，請重新下載。");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        // Validate all names before writing anything, including case-insensitive collisions.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = Resolve(staging, entry.FullName);
            if (!names.Add(path)) throw new InvalidDataException("安裝包包含重複路徑。");
        }
        Directory.CreateDirectory(staging);
        zip.ExtractToDirectory(staging);
        var manifest = ReadManifest(staging);
        var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
        if (files.Length != manifest.Files.Count + 1) throw new InvalidDataException("安裝包檔案清單不一致。");
        foreach (var (name, hash) in manifest.Files)
        {
            using var file = File.OpenRead(Resolve(staging, name));
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"檔案校驗失敗：{name}");
        }
        // Microsoft.WinUI.dll rather than Microsoft.UI.Xaml.dll: the managed projection is
        // present in both layouts, while the native XAML binary only exists in the standalone
        // one, where the whole Windows App SDK is copied into the installation. Naming the
        // native binary here would refuse every shared-runtime release, including the upgrade
        // that converts an existing standalone installation into one.
        foreach (var required in new[] { "Downlism.App.exe", "Downlism.Setup.exe", "Uninstall.exe", "Downlism.Host.exe", "coreclr.dll", "Microsoft.WinUI.dll" })
            if (!manifest.Files.ContainsKey(required)) throw new InvalidDataException($"安裝包缺少 {required}");
        return manifest;
    }

    public static void RemoveOwnedFiles(string root, IProgress<InstallationProgress>? progress = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var manifest = ReadManifest(root);
        // Resolve every path before deletion; never follow reparse points.
        var paths = manifest.Files.Keys.Select(name => Resolve(root, name)).ToArray();
        var removed = 0;
        progress?.Report(new("移除程式檔案", 0, paths.Length));
        foreach (var path in paths)
        {
            File.Delete(path);
            progress?.Report(new("移除程式檔案", ++removed, paths.Length));
        }
        var uninstallExe = Path.Combine(root, "Uninstall.exe");
        if (File.Exists(uninstallExe)) try { File.Delete(uninstallExe); } catch { }
        File.Delete(Resolve(root, ManifestName));
        // Only remove empty owned directories, preserving user-added files.
        foreach (var directory in paths.Select(Path.GetDirectoryName).OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length))
        {
            var current = directory;
            while (current.Length >= root.Length && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                current = Path.GetDirectoryName(current)!;
            }
        }
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    public static void RemoveInstallation(string root, bool keepData, IProgress<InstallationProgress>? progress = null)
    {
        if (keepData) { RemoveOwnedFiles(root, progress); return; }
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        ReadManifest(root);
        RejectReparseTree(root);
        var manifestPath = Path.Combine(root, ManifestName);
        // Retain the ownership record until every other file has been removed, so a
        // locked file does not make a partially removed installation impossible to retry.
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !string.Equals(file, manifestPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var removed = 0;
        progress?.Report(new("移除程式檔案", 0, files.Length));
        foreach (var file in files)
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            File.Delete(file);
            progress?.Report(new("移除程式檔案", ++removed, files.Length));
        }
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            Directory.Delete(directory);
        File.Delete(manifestPath);
        Directory.Delete(root);
    }
}
