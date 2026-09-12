using System.Net;

namespace Downlism.Core.Downloads;

/// <summary>
/// Decides whether a failed transfer is worth trying again, and how long to wait first.
/// </summary>
/// <remarks>
/// The single most useful thing a download manager does that a browser does not: a transfer
/// that dies eight hours in should come back by itself rather than wait for someone to notice.
/// The distinction that matters is transient versus settled — a dropped connection deserves
/// another attempt, a 404 never will, and retrying it only delays telling the user the truth.
/// </remarks>
public sealed record RetryPolicy(int MaximumAttempts = 3, double BackoffSeconds = 5, double MaximumBackoffSeconds = 120)
{
    public static RetryPolicy Default { get; } = new();

    /// <summary>Never retries; used when the user asked for a single attempt.</summary>
    public static RetryPolicy None { get; } = new(MaximumAttempts: 1);

    public bool ShouldRetry(Exception exception, int attempt) =>
        attempt < MaximumAttempts && IsTransient(exception);

    /// <summary>Exponential backoff, so a server that is down is not hammered.</summary>
    public TimeSpan DelayBefore(int attempt) => TimeSpan.FromSeconds(
        Math.Min(MaximumBackoffSeconds, BackoffSeconds * Math.Pow(2, Math.Max(0, attempt - 1))));

    /// <summary>
    /// Whether the failure looks like the network rather than the file. Anything unrecognised
    /// counts as settled: retrying an unknown fault risks looping on a bug forever.
    /// </summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        TimeoutException => true,
        IOException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => IsTransient(status),
        _ => false,
    };

    public static bool IsTransient(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        // A range request refused mid-transfer usually means the entity moved on; starting
        // over is the correct response and is worth one more attempt.
        HttpStatusCode.RequestedRangeNotSatisfiable => true,
        >= HttpStatusCode.InternalServerError => true,
        _ => false,
    };
}
