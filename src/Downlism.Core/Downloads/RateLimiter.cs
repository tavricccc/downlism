using System.Diagnostics;

namespace Downlism.Core.Downloads;

/// <summary>
/// A token bucket shared by every connection of a download, so the configured ceiling is a
/// whole-transfer limit rather than a per-connection one.
/// </summary>
public sealed class RateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _burst;
    private long _bytesPerSecond;
    private double _available;
    private long _lastTimestamp;

    /// <param name="bytesPerSecond">Zero or less means unlimited.</param>
    public RateLimiter(long bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
        // A one-second burst keeps small reads from being serialised into lockstep while
        // still holding the long-run average at the configured rate.
        _burst = Math.Max(bytesPerSecond, 64 * 1024);
        _available = _burst;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public bool IsUnlimited => Volatile.Read(ref _bytesPerSecond) <= 0;

    /// <summary>Applies a new ceiling without interrupting transfers in flight.</summary>
    public void SetLimit(long bytesPerSecond) => Volatile.Write(ref _bytesPerSecond, bytesPerSecond);

    /// <summary>Waits until <paramref name="byteCount"/> bytes may be consumed.</summary>
    public async ValueTask ConsumeAsync(int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0) return;

        while (true)
        {
            if (IsUnlimited) return;

            TimeSpan wait;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rate = Volatile.Read(ref _bytesPerSecond);
                if (rate <= 0) return;

                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now);
                _lastTimestamp = now;
                _available = Math.Min(_burst, _available + elapsed.TotalSeconds * rate);

                if (_available >= byteCount)
                {
                    _available -= byteCount;
                    return;
                }

                wait = TimeSpan.FromSeconds((byteCount - _available) / rate);
            }
            finally
            {
                _gate.Release();
            }

            // Sleep outside the lock so other connections can keep draining the bucket.
            await Task.Delay(Min(wait, TimeSpan.FromSeconds(1)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
