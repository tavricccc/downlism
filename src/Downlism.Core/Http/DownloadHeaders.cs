using Downlism.Core.Downloads;

namespace Downlism.Core.Http;

internal static class DownloadHeaders
{
    public static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme &&
        left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    public static void Apply(HttpRequestMessage message, DownloadRequest source)
    {
        DownloadHttpClientFactory.PinToHttp11(message);
        message.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        // Raw browser cookies have no domain metadata. Never forward them to a redirect host.
        if (SameOrigin(source.Uri, message.RequestUri!) && !string.IsNullOrEmpty(source.Cookies))
            message.Headers.TryAddWithoutValidation("Cookie", Clean(source.Cookies));
        if (Uri.TryCreate(source.Referrer, UriKind.Absolute, out var referrer) && referrer.Scheme is "http" or "https" &&
            !(referrer.Scheme == "https" && message.RequestUri!.Scheme == "http"))
            message.Headers.Referrer = referrer;
        if (!string.IsNullOrWhiteSpace(source.UserAgent)) message.Headers.TryAddWithoutValidation("User-Agent", Clean(source.UserAgent));
    }

    private static string Clean(string value) => value.Contains('\r') || value.Contains('\n')
        ? throw new ArgumentException("HTTP 標頭不可包含換行字元。") : value;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage message,
        int timeoutSeconds, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600)));
        try { return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("伺服器未在指定時間內回應標頭。"); }
    }
}
