namespace Downlism.Core.Downloads;

/// <summary>Byte-weighted transfer average across active sampling intervals, not an average of rates.</summary>
public sealed class DownloadAverage
{
    private long? _previousBytes;
    private TimeSpan _previousTime;
    private double _bytes;
    private double _seconds;

    public double BytesPerSecond => _seconds > 0 ? _bytes / _seconds : 0;
    public double TransferredBytes => _bytes;
    public double ActiveSeconds => _seconds;

    public void Restore(double bytes, double seconds)
    {
        _bytes = double.IsFinite(bytes) && bytes >= 0 ? bytes : 0;
        _seconds = double.IsFinite(seconds) && seconds >= 0 ? seconds : 0;
        BreakInterval();
    }

    // A resumed attempt establishes a new baseline so bytes already on disk are not counted.
    // Pauses, queue waits, retry backoff and tool provisioning are outside sampling intervals.
    public void BreakInterval() => _previousBytes = null;

    public void Observe(long completedBytes, TimeSpan timestamp)
    {
        if (completedBytes < 0) return;
        if (_previousBytes is { } previous && completedBytes >= previous && timestamp > _previousTime)
        {
            _bytes += completedBytes - previous;
            _seconds += (timestamp - _previousTime).TotalSeconds;
        }
        _previousBytes = completedBytes;
        _previousTime = timestamp;
    }
}
