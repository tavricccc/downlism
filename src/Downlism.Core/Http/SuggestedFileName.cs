using System.Globalization;
using System.Text;

namespace Downlism.Core.Http;

/// <summary>
/// Derives the file name to save under. Order of preference follows what browsers do:
/// <c>Content-Disposition</c> first, then the final URL path, then a fallback.
/// </summary>
public static class SuggestedFileName
{
    private const string Fallback = "download";

    /// <summary>Parses <c>Content-Disposition</c>, preferring RFC 5987 <c>filename*</c> over <c>filename</c>.</summary>
    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        string? plain = null;
        foreach (var parameter in SplitParameters(header))
        {
            var equals = parameter.IndexOf('=');
            if (equals <= 0) continue;

            var name = parameter[..equals].Trim();
            var value = parameter[(equals + 1)..].Trim();

            // filename* wins outright, so return as soon as one parses.
            if (name.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                var extended = DecodeExtendedValue(value);
                if (extended is not null) return Sanitize(extended);
            }
            else if (name.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                plain ??= Unquote(value);
            }
        }

        return plain is null ? null : Sanitize(plain);
    }

    /// <summary>Falls back to the last path segment of the final (post-redirect) URL.</summary>
    public static string FromUri(Uri uri)
    {
        // A magnet link has no path at all. Its display name is the only readable thing in it,
        // and a row labelled with a forty-character info hash is worse than one labelled
        // "download".
        if (uri.Scheme == Downloads.TransferRouting.MagnetScheme)
        {
            return Sanitize(Downloads.TransferRouting.NameFromMagnet(uri) ?? Fallback);
        }

        var path = uri.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        var segment = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        if (segment.Length == 0) return Fallback;

        try
        {
            segment = Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            // Keep the escaped form rather than failing the download over a name.
        }

        return Sanitize(segment);
    }

    /// <summary>
    /// Strips directory separators and characters Windows rejects. The header is attacker
    /// controlled, so a name like <c>..\..\autorun.inf</c> must never reach the file system.
    /// </summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fallback;

        // Take the last path segment: defeats both separator styles at once.
        var cut = name.AsSpan().LastIndexOfAny('/', '\\', ':');
        if (cut >= 0) name = name[(cut + 1)..];

        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format || Path.GetInvalidFileNameChars().Contains(character)
                ? '_'
                : character);
        }

        // Windows ignores trailing dots and spaces, so "evil.exe. " would resolve to "evil.exe".
        var sanitized = builder.ToString().TrimEnd('.', ' ').Trim();
        if (sanitized.Length == 0 || sanitized is "." or "..") return Fallback;

        var stem = sanitized.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
            sanitized = "_" + sanitized;
        // Leave room for the partial/sidecar suffix and collision numbering on NTFS.
        if (sanitized.Length <= 220) return sanitized;
        var extension = Path.GetExtension(sanitized);
        if (extension.Length > 32) extension = "";
        var count = 220 - extension.Length;
        if (char.IsHighSurrogate(sanitized[count - 1])) count--;
        return sanitized[..count] + extension;
    }

    /// <summary>Splits on semicolons that sit outside quoted strings.</summary>
    private static IEnumerable<string> SplitParameters(string header)
    {
        var start = 0;
        var quoted = false;
        for (var index = 0; index < header.Length; index++)
        {
            var character = header[index];
            if (character == '"' && (index == 0 || header[index - 1] != '\\')) quoted = !quoted;
            else if (character == ';' && !quoted)
            {
                yield return header[start..index];
                start = index + 1;
            }
        }

        if (start < header.Length) yield return header[start..];
    }

    /// <summary>Decodes RFC 5987 <c>UTF-8''%E4%B8%AD.txt</c>.</summary>
    private static string? DecodeExtendedValue(string value)
    {
        var parts = value.Split('\'', 3);
        if (parts.Length != 3) return null;

        Encoding encoding;
        try
        {
            encoding = parts[0].Length == 0 ? Encoding.UTF8 : Encoding.GetEncoding(parts[0]);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var bytes = new List<byte>(parts[2].Length);
        for (var index = 0; index < parts[2].Length; index++)
        {
            if (parts[2][index] == '%' && index + 2 < parts[2].Length
                && byte.TryParse(parts[2].AsSpan(index + 1, 2), NumberStyles.HexNumber, null, out var decoded))
            {
                bytes.Add(decoded);
                index += 2;
            }
            else
            {
                bytes.AddRange(encoding.GetBytes(parts[2][index].ToString()));
            }
        }

        var result = encoding.GetString(bytes.ToArray());
        return result.Length == 0 ? null : result;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Replace("\\\"", "\"");
        }

        return value;
    }
}
