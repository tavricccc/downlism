using System.Security.Cryptography;

namespace Downlism.Core.Downloads;

/// <summary>
/// Hashes a finished file so it can be compared with a checksum published beside the download.
/// </summary>
/// <remarks>
/// The reason to put this in a download manager rather than leave it to a separate tool is
/// that the moment someone wants it is the moment the file arrives. A mirror serving a
/// truncated or substituted file looks exactly like a successful download otherwise.
/// </remarks>
public static class FileHash
{
    public enum Algorithm
    {
        Sha256,
        Sha1,
        Md5,
    }

    public static async Task<string> ComputeAsync(
        string path,
        Algorithm algorithm,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using HashAlgorithm hasher = algorithm switch
        {
            Algorithm.Sha1 => SHA1.Create(),
            Algorithm.Md5 => MD5.Create(),
            _ => SHA256.Create(),
        };

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);

        var total = stream.Length;
        var buffer = new byte[64 * 1024];
        var read = 0L;

        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;

            hasher.TransformBlock(buffer, 0, count, null, 0);
            read += count;

            // A multi-gigabyte file takes long enough that silence reads as a hang.
            if (total > 0) progress?.Report((double)read / total);
        }

        hasher.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hasher.Hash ?? []);
    }

    /// <summary>
    /// Compares against a value pasted from a download page, which arrives in whatever shape
    /// that page used: spaced, upper case, or with the file name appended as in sha256sum
    /// output.
    /// </summary>
    public static bool Matches(string computed, string expected)
    {
        var wanted = Normalize(expected);
        return wanted.Length > 0 && string.Equals(Normalize(computed), wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return string.Empty;

        // Two shapes appear in the wild and they need opposite treatment. A checksum printed
        // in groups ("BA 78 16 BF") is one value split by spaces and must be joined; sha256sum
        // output ("ba7816bf  ubuntu.iso") is a value followed by a name and must be cut. Simply
        // keeping every hex character would turn the "f" in "file.iso" into part of the hash.
        if (tokens.All(IsHex)) return string.Concat(tokens);

        return Array.Find(tokens, IsHex) ?? string.Empty;
    }

    private static bool IsHex(string token)
    {
        foreach (var character in token)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }

        return token.Length > 0;
    }
}
