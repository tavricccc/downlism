namespace Downlism.Core.Downloads;

public sealed record LinkList(IReadOnlyList<Uri> Links, int Duplicates)
{
    public static LinkList Parse(string text, int maximum = 500)
    {
        if (text.Length > 1_048_576) throw new ArgumentException("網址清單最多 1 MiB。");
        var links = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var lineNumber = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Any(char.IsWhiteSpace) || !TransferRouting.TryParse(line, out var uri) || (uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.UserInfo)))
                throw new ArgumentException($"第 {lineNumber} 行不是有效的 HTTP、HTTPS 或 magnet 網址。每行只能放一個網址，且不可內嵌帳號密碼。");
            if (!seen.Add(uri.AbsoluteUri)) { duplicates++; continue; }
            links.Add(uri);
            if (links.Count > maximum) throw new ArgumentException($"一次最多加入 {maximum} 個網址。");
        }
        if (links.Count == 0) throw new ArgumentException("請輸入至少一個網址，每行一個。");
        return new(links, duplicates);
    }
}
