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
        _instanceGate = new SingleInstanceGate("Downlism.App", () => dispatcher.TryEnqueue(Raise));
        if (!_instanceGate.IsPrimary)
        {
            _instanceGate.Dispose();
            Exit();
            return;
        }

        _window = new MainWindow();

        // The installer launches the first run with this switch. The extension cannot install
        // itself, and the minute after installing is the only minute anyone is willing to
        // follow four steps in chrome://extensions. Requested before the window is activated,
        // because the window only promises to honour it while it is still unshown.
        if (LoginStartupService.StartedForExtensionGuide())
        {
            _window.ShowExtensionGuideOnFirstFrame();

            // Turned on once, on the launch that follows a fresh install. The extension hands
            // downloads to whatever is listening, so a Downlism that is not running after a
            // reboot quietly gives every download back to the browser -- which looks like the
            // app failing rather than like a setting nobody switched on. Visible and
            // reversible in both the tray menu and the settings flyout.
            // Startup registration is now an explicit preference, not an install side effect.
            _window.RefreshLaunchAtLogin();
        }

        // Closing the window hides it. Transfers continue, the browser can still hand new ones
        // over, and the tray is where the app is actually quit.
        _window.AppWindow.Closing += (_, closing) =>
        {
            if (_exiting) return;
            closing.Cancel = true;
            if (_window.Settings.CloseToTray) ShowWindow(Handle, Hide);
            else dispatcher.TryEnqueue(ExitApplication);
        };

        _tray = new TrayIcon { LaunchesAtLogin = _loginStartup.IsEnabled() };
        _tray.ShowRequested += (_, _) => dispatcher.TryEnqueue(Raise);
        _tray.PauseAllRequested += (_, _) => Queue.PauseAll();
        _tray.LaunchAtLoginToggled += (_, _) => dispatcher.TryEnqueue(ToggleLaunchAtLogin);
        _tray.ExitRequested += (_, _) => dispatcher.TryEnqueue(ExitApplication);

        // The same switch exists in the settings flyout; whichever one is used, the other has
        // to stop showing the old answer.
        _window.LaunchAtLoginChanged += (_, _) => _tray.LaunchesAtLogin = _loginStartup.IsEnabled();

        Queue.Changed += OnQueueChanged;
        _trayRefresh = dispatcher.CreateTimer();
        _trayRefresh.Interval = TimeSpan.FromMilliseconds(500);
        _trayRefresh.Tick += (_, _) =>
        {
            var active = Queue.Jobs.Where(job => job.State == DownloadState.Running).ToArray();
            _tray.UpdateTooltip(active.Length, active.Sum(job => job.Progress?.BytesPerSecond ?? 0));
        };
        _trayRefresh.Start();

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
        _ingest = new IngestListener(_window.AddFromBrowser, () => _window!.Settings);
        _ingest.Start();

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
        if (_window is null) return;

        ShowWindow(Handle, Restore);
        _window.Activate();
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
