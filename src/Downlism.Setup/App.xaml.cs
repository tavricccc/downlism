using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Downlism.Core.Lifecycle;

namespace Downlism.Setup;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    private Window? _window;
    private SingleInstanceGate? _instanceGate;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _instanceGate = new SingleInstanceGate("Downlism.Setup", () => dispatcher.TryEnqueue(() =>
        {
            if (_window is null) return;
            ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), 9);
            _window.Activate();
        }));
        if (!_instanceGate.IsPrimary) { _instanceGate.Dispose(); Exit(); return; }
        _window = new MainWindow();
        _window.Closed += (_, _) => { _window = null; _instanceGate.Dispose(); };
        _window.Activate();
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), 5);
        _window.Activate();
    }
}
