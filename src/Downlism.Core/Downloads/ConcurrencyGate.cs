namespace Downlism.Core.Downloads;

/// <summary>A FIFO gate whose limit changes without creating a second pool of slots.</summary>
public sealed class ConcurrencyGate
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource<IDisposable>> _waiting = new();
    private int _active;
    private int _limit;
    public ConcurrencyGate(int limit) => _limit = Math.Clamp(limit, 1, 16);

    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var waiter = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) { _waiting.Enqueue(waiter); Drain(); }
        using var registration = token.Register(() =>
        {
            lock (_sync) { waiter.TrySetCanceled(token); Drain(); }
        });
        return await waiter.Task.ConfigureAwait(false);
    }

    public void SetLimit(int limit)
    {
        lock (_sync) { _limit = Math.Clamp(limit, 1, 16); Drain(); }
    }

    private void Drain()
    {
        while (_waiting.TryPeek(out var waiter))
        {
            if (waiter.Task.IsCompleted) { _waiting.Dequeue(); continue; }
            if (_active >= _limit) return;
            _waiting.Dequeue();
            _active++;
            waiter.SetResult(new Lease(this));
        }
    }

    private void Release()
    {
        lock (_sync) { _active--; Drain(); }
    }

    private sealed class Lease(ConcurrencyGate owner) : IDisposable
    {
        private ConcurrencyGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
