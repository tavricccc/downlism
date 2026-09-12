namespace Downlism.Core.Downloads;

/// <summary>
/// Sorts finished downloads into folders by what they are.
/// </summary>
/// <remarks>
/// The Downloads folder becomes unusable within a few months precisely because everything
/// lands in it. Sorting on the way in is the one moment it costs nothing; asking someone to
/// tidy it later means it never happens.
/// </remarks>
public sealed record DownloadCategory(string Name, IReadOnlySet<string> Extensions)
{
    public static readonly DownloadCategory Video = new("影片",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts" });

    public static readonly DownloadCategory Audio = new("音樂",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus" });

    public static readonly DownloadCategory Documents = new("文件",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "epub", "mobi", "txt", "csv" });

    public static readonly DownloadCategory Archives = new("壓縮檔",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "tgz", "iso", "img" });

    public static readonly DownloadCategory Programs = new("程式",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "exe", "msi", "msix", "appx", "apk", "deb", "rpm", "dmg", "pkg", "appimage" });

    public static readonly DownloadCategory Images = new("圖片",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "svg", "heic", "psd" });

    /// <summary>Anything unrecognised, kept in the root rather than in a folder called "其他".</summary>
    public static readonly DownloadCategory Other = new("", new HashSet<string>());

    public static IReadOnlyList<DownloadCategory> All { get; } =
        [Video, Audio, Documents, Archives, Programs, Images];

    public static DownloadCategory For(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        if (extension.Length == 0) return Other;

        foreach (var category in All)
        {
            if (category.Extensions.Contains(extension)) return category;
        }

        return Other;
    }

    /// <summary>
    /// The folder a file should be saved into. An uncategorised file stays in the root, so an
    /// unfamiliar extension never gets buried somewhere the user would not think to look.
    /// </summary>
    public static string DirectoryFor(string root, string fileName, bool sortIntoFolders)
    {
        if (!sortIntoFolders) return root;

        var category = For(fileName);
        return category.Name.Length == 0 ? root : Path.Combine(root, category.Name);
    }
}
