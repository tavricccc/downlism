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
    private DispatcherQueueTimer? _trayRefresh;

    public static Downlism.Core.Settings.AppSettings CurrentSettings =>
        ((App)Current)._window?.Settings ?? Downlism.Core.Settings.AppSettings.Load();
    public static string ThemePreference { get; private set; } = "System";
    public static void ApplyTheme(FrameworkElement root, string theme)
    {
        ThemePreference = theme;
        root.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    public App() => InitializeComponent();

    /// <summary>The queue outlives any window, so closing the window does not abandon transfers.</summary>
    public static DownloadQueue Queue { get; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        // Two instances would fight over the same partial files and the same ingest pipe, so a
        // second launch raises the existing window instead of opening another one.
#if DEBUG
        var isolatedMemoryTest = Environment.GetEnvironmentVariable("DOWNLISM_MEMORY_TEST") == "1";
        var gateName = isolatedMemoryTest ? "Downlism.App.MemoryTest" : "Downlism.App";
#else
        const bool isolatedMemoryTest = false;
        const string gateName = "Downlism.App";
#endif
        _instanceGate = new SingleInstanceGate(gateName, () => dispatcher.TryEnqueue(Raise));
        if (!_instanceGate.IsPrimary)
        {
            _instanceGate.Dispose();
            Exit();
            return;
        }

        _tray = new TrayIcon { LaunchesAtLogin = _loginStartup.IsEnabled() };
        _tray.ShowRequested += (_, _) => dispatcher.TryEnqueue(Raise);
        _tray.PauseAllRequested += (_, _) => Queue.PauseAll();
        _tray.LaunchAtLoginToggled += (_, _) => dispatcher.TryEnqueue(ToggleLaunchAtLogin);
        _tray.ExitRequested += (_, _) => dispatcher.TryEnqueue(ExitApplication);

        Queue.Changed += OnQueueChanged;
        _trayRefresh = dispatcher.CreateTimer();
        _trayRefresh.Interval = TimeSpan.FromMilliseconds(500);
        _trayRefresh.Tick += (_, _) =>
        {
            var active = Queue.RunningSummary();
            _tray.UpdateTooltip(active.Count, active.BytesPerSecond);
        };
        _trayRefresh.Start();

        // Lets the installer ask this process to release its files before an update.
        _shutdownSignal = new AppShutdownSignal(
            Environment.ProcessId,
            () => dispatcher.TryEnqueue(ExitApplication));

        var background = LoginStartupService.StartedInBackground();
        var extensionGuide = LoginStartupService.StartedForExtensionGuide();
        if (!background || extensionGuide || Downlism.Core.Settings.AppSettings.Load().ResumeOnStartup)
        {
            var window = EnsureWindow();
            if (extensionGuide) window.ShowExtensionGuideOnFirstFrame();
            window.Activate();
            ShowWindow(Handle, background ? Hide : ShowNormal);
        }

        // A browser handoff creates the main window only when one is needed to show the download.
        _ingest = new IngestListener(
            capture => dispatcher.TryEnqueue(() => EnsureWindow().AddFromBrowser(capture)),
            () => _window?.Settings ?? Downlism.Core.Settings.AppSettings.Load(),
            isolatedMemoryTest ? "Downlism.Ingest.MemoryTest" : null);
        _ingest.Start();
    }

    private MainWindow EnsureWindow()
    {
        if (_window is not null) return _window;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var window = new MainWindow();
        _window = window;
        window.AppWindow.Closing += (_, closing) =>
        {
            if (_exiting) return;
            closing.Cancel = true;
            if (window.Settings.CloseToTray) ShowWindow(Handle, Hide);
            else dispatcher.TryEnqueue(ExitApplication);
        };
        window.LaunchAtLoginChanged += (_, _) => { if (_tray is not null) _tray.LaunchesAtLogin = _loginStartup.IsEnabled(); };
        return window;
    }

    private nint Handle => _window is null
        ? 0
        : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    private void OnQueueChanged(DownloadJob job)
    {
        var state = job.State;
        if (state is DownloadState.Running or DownloadState.Removed)
        {
            _announced.TryRemove(job.Id, out _);
            return;
        }
        if (state is not (DownloadState.Failed or DownloadState.Completed) || !_announced.TryAdd(job.Id, 0)) return;
        var title = state == DownloadState.Completed ? "下載完成" : "下載失敗";
        var message = state == DownloadState.Completed ? job.FileName : $"{job.FileName}：{job.Error}";
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is null) return;
            if ((state == DownloadState.Completed && _window.Settings.NotifyOnComplete) ||
                (state == DownloadState.Failed && _window.Settings.NotifyOnFailure)) _tray?.Announce(title, message);
        });
    }

    /// <summary>Jobs already announced, so a later state change does not repeat the balloon.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _announced = new();

    private void EnableLoginStartupQuietly()
    {
        try
        {
            _loginStartup.SetEnabled(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            // A first run that cannot write the Run key is still a perfectly good first run.
        }
    }

    private void ToggleLaunchAtLogin()
    {
        if (_tray is null) return;

        try
        {
            var enable = !_tray.LaunchesAtLogin;
            _loginStartup.SetEnabled(enable);
            _tray.LaunchesAtLogin = enable;
            _window?.RefreshLaunchAtLogin();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            // The menu simply stays as it was; nothing here has a surface to report on.
            _tray.LaunchesAtLogin = _loginStartup.IsEnabled();
        }
    }

    private void Raise()
    {
        var window = EnsureWindow();
        window.Activate();
        ShowWindow(Handle, Restore);
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        Queue.Changed -= OnQueueChanged;
        _trayRefresh?.Stop();
        Queue.PauseAll();
        _window?.SaveBeforeExit();
        _ingest?.Dispose();
        _shutdownSignal?.Dispose();
        _tray?.Dispose();
        Queue.Dispose();
        _instanceGate?.Dispose();
        _window?.Close();

        Exit();
    }
}
