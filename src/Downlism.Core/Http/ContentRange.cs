using System.Globalization;

namespace Downlism.Core.Http;

/// <summary>A parsed <c>Content-Range</c> response header.</summary>
public readonly record struct ContentRange(long Start, long End, long? TotalLength)
{
    /// <summary>
    /// Parses <c>bytes 0-0/12345</c>. The total is null when the server sends
    /// <c>*</c>, which means it honours ranges but will not commit to a size.
    /// </summary>
    public static bool TryParse(string? value, out ContentRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var span = value.AsSpan().Trim();
        const string UnitPrefix = "bytes ";
        if (!span.StartsWith(UnitPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        span = span[UnitPrefix.Length..].Trim();

        var slash = span.IndexOf('/');
        if (slash < 0) return false;

        var rangePart = span[..slash].Trim();
        var totalPart = span[(slash + 1)..].Trim();

        var dash = rangePart.IndexOf('-');
        if (dash <= 0) return false;

        if (!long.TryParse(rangePart[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start)) return false;
        if (!long.TryParse(rangePart[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end)) return false;
        if (end < start) return false;

        long? total = null;
        if (!totalPart.SequenceEqual("*"))
        {
            if (!long.TryParse(totalPart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
            total = parsed;
        }

        range = new ContentRange(start, end, total);
        return true;
    }
}
