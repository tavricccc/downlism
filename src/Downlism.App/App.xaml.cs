using System.Runtime.InteropServices;
using Downlism.App.Services;
using Downlism.Core.Lifecycle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Downlism.App;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    private const int ShowNormal = 5;
    private const int Restore = 9;

    private MainWindow? _window;
    private SingleInstanceGate? _instanceGate;
    private IngestListener? _ingest;

    public App() => InitializeComponent();

    /// <summary>The queue outlives any window, so a closed window does not abandon transfers.</summary>
    public static DownloadQueue Queue { get; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        // Two instances would fight over the same partial files and the same ingest pipe, so a
        // second launch raises the existing window instead of opening another one.
        _instanceGate = new SingleInstanceGate("Downlism.App", () => dispatcher.TryEnqueue(Raise));
        if (!_instanceGate.IsPrimary)
        {
            _instanceGate.Dispose();
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            _ingest?.Dispose();
            _instanceGate?.Dispose();
            Queue.Dispose();
        };

        _window.Activate();
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), ShowNormal);

        // The listener starts after the window exists, so a download arriving during startup
        // has somewhere to appear.
        _ingest = new IngestListener(Queue, _window.AddFromBrowser);
        _ingest.Start();
    }

    private void Raise()
    {
        if (_window is null) return;

        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), Restore);
        _window.Activate();
    }
}
