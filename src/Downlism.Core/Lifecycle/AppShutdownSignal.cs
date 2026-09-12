namespace Downlism.Core.Lifecycle;

/// <summary>Lets maintenance ask a specific process to release its tray, hotkey and files.</summary>
public sealed class AppShutdownSignal : IDisposable
{
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;

    public AppShutdownSignal(int processId, Action onShutdown)
    {
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name(processId));
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => onShutdown(),
            null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public static bool Request(int processId)
    {
        if (!EventWaitHandle.TryOpenExisting(Name(processId), out var signal)) return false;
        using (signal) return signal.Set();
    }

    private static string Name(int processId) => $@"Local\Downlism.Shutdown.{processId}";

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
    }
}
