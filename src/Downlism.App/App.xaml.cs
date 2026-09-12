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

    private const int Hide = 0;
    private const int ShowNormal = 5;
    private const int Restore = 9;

    private readonly LoginStartupService _loginStartup = new();
    private MainWindow? _window;
    private SingleInstanceGate? _instanceGate;
    private IngestListener? _ingest;
    private TrayIcon? _tray;
    private AppShutdownSignal? _shutdownSignal;
    private bool _exiting;

    public App() => InitializeComponent();

    /// <summary>The queue outlives any window, so closing the window does not abandon transfers.</summary>
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

        // Closing the window hides it. Transfers continue, the browser can still hand new ones
        // over, and the tray is where the app is actually quit.
        _window.AppWindow.Closing += (_, closing) =>
        {
            if (_exiting) return;
            closing.Cancel = true;
            ShowWindow(Handle, Hide);
        };

        _tray = new TrayIcon { LaunchesAtLogin = _loginStartup.IsEnabled() };
        _tray.ShowRequested += (_, _) => dispatcher.TryEnqueue(Raise);
        _tray.PauseAllRequested += (_, _) => Queue.PauseAll();
        _tray.LaunchAtLoginToggled += (_, _) => dispatcher.TryEnqueue(ToggleLaunchAtLogin);
        _tray.ExitRequested += (_, _) => dispatcher.TryEnqueue(ExitApplication);

        Queue.Changed += OnQueueChanged;

        // Lets the installer ask this process to release its files before an update.
        _shutdownSignal = new AppShutdownSignal(
            Environment.ProcessId,
            () => dispatcher.TryEnqueue(ExitApplication));

        // Started by the Run key at sign-in: take the tray, leave the screen alone.
        if (LoginStartupService.StartedInBackground())
        {
            _window.Activate();
            ShowWindow(Handle, Hide);
        }
        else
        {
            _window.Activate();
            ShowWindow(Handle, ShowNormal);
        }

        // The listener starts after the window exists, so a download arriving during startup
        // has somewhere to appear.
        _ingest = new IngestListener(Queue, _window.AddFromBrowser, () => _window!.Settings);
        _ingest.Start();
    }

    private nint Handle => _window is null
        ? 0
        : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    private void OnQueueChanged(DownloadJob job)
    {
        if (_tray is null) return;

        var active = Queue.Jobs.Where(entry => entry.State == DownloadState.Running).ToArray();
        var speed = active.Sum(entry => entry.Progress?.BytesPerSecond ?? 0);
        _tray.UpdateTooltip(active.Length, speed);

        // Told once, when it finishes. The whole point of leaving the window closed is not
        // having to watch it.
        if (job.State == DownloadState.Completed && _announced.Add(job.Id))
        {
            _tray.Announce("下載完成", job.FileName);
        }
        else if (job.State == DownloadState.Failed && _announced.Add(job.Id))
        {
            _tray.Announce("下載失敗", $"{job.FileName}：{job.Error}");
        }
    }

    /// <summary>Jobs already announced, so a later state change does not repeat the balloon.</summary>
    private readonly HashSet<Guid> _announced = [];

    private void ToggleLaunchAtLogin()
    {
        if (_tray is null) return;

        try
        {
            var enable = !_tray.LaunchesAtLogin;
            _loginStartup.SetEnabled(enable);
            _tray.LaunchesAtLogin = enable;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            // The menu simply stays as it was; nothing here has a surface to report on.
            _tray.LaunchesAtLogin = _loginStartup.IsEnabled();
        }
    }

    private void Raise()
    {
        if (_window is null) return;

        ShowWindow(Handle, Restore);
        _window.Activate();
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        Queue.Changed -= OnQueueChanged;
        _ingest?.Dispose();
        _shutdownSignal?.Dispose();
        _tray?.Dispose();
        Queue.Dispose();
        _instanceGate?.Dispose();
        _window?.Close();

        Exit();
    }
}
