using Downlism.Core.Http;

namespace Downlism.Core.Downloads;

/// <summary>One safe subfolder and its extensions. No absolute paths or traversal in rules.</summary>
public sealed record CategoryRule(string Folder, IReadOnlySet<string> Extensions)
{
    public static IReadOnlyList<CategoryRule> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Length > 16_384) throw new ArgumentException("分類規則最多 16,384 個字元。");
        var result = new List<CategoryRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                     .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || parts[0] is "." or ".." ||
                parts[0].Length > 80 || IsReserved(parts[0]) || SuggestedFileName.Sanitize(parts[0]) != parts[0] ||
                parts[0].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"分類規則格式錯誤：{line}。請使用「資料夾=zip,7z」。");
            var extensions = parts[1].Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.TrimStart('.').ToLowerInvariant()).ToArray();
            if (extensions.Length == 0 || extensions.Any(value => value.Length > 20 || !value.All(char.IsAsciiLetterOrDigit)))
                throw new ArgumentException($"「{parts[0]}」需要有效的副檔名，例如 zip,7z。");
            foreach (var extension in extensions)
                if (!seen.Add(extension)) throw new ArgumentException($"副檔名 {extension} 出現超過一次，請只指定一個資料夾。");
            result.Add(new(parts[0], new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase)));
            if (result.Count > 100) throw new ArgumentException("最多可設定 100 個分類。");
        }
        return result;
    }

    private static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             stem[3] is >= '1' and <= '9');
    }
}
