namespace Downlism.Core.Downloads;

/// <summary>Recent throughput over a two-second window; callers serialize access.</summary>
public sealed class DownloadRate
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);
    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();
    private long? _previous;
    private TimeSpan _started;
    private double _bytes;

    public void Reset()
    {
        _samples.Clear();
        _previous = null;
        _bytes = 0;
    }

    public double Observe(long completed, TimeSpan now)
    {
        if (completed < 0) return Current(now);
        if (_previous is null || completed < _previous)
        {
            Reset();
            _started = now;
        }
        else
        {
            var delta = completed - _previous.Value;
            if (delta > 0) { _samples.Enqueue((now, delta)); _bytes += delta; }
        }
        _previous = completed;
        return Current(now);
    }

    public double Current(TimeSpan now)
    {
        while (_samples.TryPeek(out var sample) && now - sample.At >= Window)
        {
            _bytes -= sample.Bytes;
            _samples.Dequeue();
        }
        var seconds = Math.Min(Window.TotalSeconds, (now - _started).TotalSeconds);
        return _previous is not null && seconds > 0 ? Math.Max(0, _bytes) / seconds : 0;
    }
}
