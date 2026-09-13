using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Downlism.App.Services;
using Downlism.App.ViewModels;
using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Downlism.App;

/// <summary>
/// The download prompt and progress window (IDM style).
/// Appears in foreground and topmost before user interaction.
/// Upon starting a download, transitions into an in-place progress monitor
/// and remains open on completion without system notifications.
/// </summary>
public sealed partial class NewDownloadWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>Each open prompt is offset so a burst of captures does not stack into one.</summary>
    private const int CascadeStep = 28;

    /// <summary>
    /// Wide enough for a folder path and a browse button on one line. Everything else is
    /// measured against it.
    /// </summary>
    private const double ContentWidth = 660;

    private static int _open;

    private readonly CaptureRequest _capture;
    private readonly Func<DownloadRequest, bool, DownloadJob> _accepted;
    private readonly Action<bool>? _stopAsking;
    private readonly int _position;
    private bool _settled;
    private DownloadJob? _job;

    public NewDownloadWindow(
        CaptureRequest capture,
        Func<DownloadRequest, bool, DownloadJob> accepted,
        Action<bool>? stopAsking = null)
    {
        InitializeComponent();

        _capture = capture;
        _accepted = accepted;
        _stopAsking = stopAsking;
        _position = Interlocked.Increment(ref _open);

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon("Assets/Downlism.ico");

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Nothing here rewards resizing, and a maximised prompt would be absurd. Left
            // resizable until the content has been measured, because a presenter that has been
            // told the window cannot be resized ignores the resize that fits it.
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            // Topmost before user interaction so it floats above browser window
            presenter.IsAlwaysOnTop = true;
        }

        Fill();
        StopAsking.Visibility = stopAsking is null ? Visibility.Collapsed : Visibility.Visible;

        Root.Loaded += (_, _) => FitToContent();

        Closed += (_, _) =>
        {
            App.Queue.Changed -= OnQueueChanged;
            if (_job is null)
            {
                Settle(start: null);
            }
        };
    }

    /// <summary>
    /// Ensures the prompt window is brought to foreground and receives input focus.
    /// </summary>
    public void ForceForeground()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (handle == 0) return;

        var foregroundHwnd = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
        var currentThread = GetCurrentThreadId();

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            AttachThreadInput(currentThread, foregroundThread, true);
            BringWindowToTop(handle);
            ShowWindow(handle, 5);
            SetForegroundWindow(handle);
            AttachThreadInput(currentThread, foregroundThread, false);
        }
        else
        {
            BringWindowToTop(handle);
            ShowWindow(handle, 5);
            SetForegroundWindow(handle);
        }
    }

    /// <summary>
    /// Sizes the window to whatever the visible content needs, then centres it.
    /// </summary>
    private void FitToContent()
    {
        var targetElement = ProgressContainer.Visibility == Visibility.Visible
            ? (FrameworkElement)ProgressContainer
            : (FrameworkElement)PromptContainer;

        targetElement.Measure(new Windows.Foundation.Size(ContentWidth, double.PositiveInfinity));

        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var height = Math.Max(targetElement.DesiredSize.Height, 140);

        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = true;

        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
            (int)Math.Round(ContentWidth * scale),
            (int)Math.Ceiling(height * scale)));

        Centre();

        if (AppWindow.Presenter is OverlappedPresenter presenter2) presenter2.IsResizable = false;
    }

    private void Centre()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        var cascade = _position % 5 * CascadeStep;

        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2) + cascade,
            work.Y + Math.Max(0, (work.Height - size.Height) / 3) + cascade));
    }

    private void Fill()
    {
        var request = _capture.Request;

        KindLabel.Text = request.Kind switch
        {
            TransferKind.Media => "影片",
            TransferKind.Torrent => "BT",
            _ => "檔案",
        };

        FileName.Text = request.FileName ?? SuggestedFileName.FromUri(request.Uri);
        FileName.IsEnabled = request.Kind == TransferKind.Http;

        Source.Text = request.Uri.Scheme == TransferRouting.MagnetScheme
            ? request.Uri.OriginalString
            : request.Uri.AbsoluteUri;
        ToolTipService.SetToolTip(Source, Source.Text);

        Size.Text = Describe(request.Kind, _capture.ExpectedBytes);

        Folder.Text = request.Directory;
        SortIntoCategories.IsChecked = request.SortIntoCategories;
    }

    private static string Describe(TransferKind kind, long bytes) => kind switch
    {
        TransferKind.Media => "開始下載後才會知道（由 yt-dlp 選擇畫質）",
        TransferKind.Torrent => "取得種子資訊後才會知道",
        _ => bytes > 0 ? DownloadRowViewModel.Bytes(bytes) : "開始下載後才會知道",
    };

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) Folder.Text = folder.Path;
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        if (_settled) return;
        _settled = true;

        Interlocked.Decrement(ref _open);

        _stopAsking?.Invoke(StopAsking.IsChecked == true);

        // User operated the prompt: unpin topmost and allow minimize during downloading!
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsMinimizable = true;
        }

        var name = FileName.Text.Trim();
        var request = _capture.Request with
        {
            FileName = _capture.Request.Kind == TransferKind.Http && name.Length > 0 ? name : null,
            Directory = Folder.Text,
            SortIntoCategories = SortIntoCategories.IsChecked == true,
        };

        _job = _accepted(request, true);

        // Switch to progress view (like IDM)
        PromptContainer.Visibility = Visibility.Collapsed;
        ProgressContainer.Visibility = Visibility.Visible;

        ProgressKindLabel.Text = KindLabel.Text;
        ProgressKindChip.Visibility = KindLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressFileName.Text = _job.FileName;
        ProgressFolder.Text = $"存到：{_job.Request.Directory}";
        Title = $"{_job.FileName} - 下載中";

        FitToContent();

        App.Queue.Changed += OnQueueChanged;
        UpdateProgress(_job);
    }

    private void OnQueueChanged(DownloadJob job)
    {
        if (_job is null || job.Id != _job.Id) return;
        DispatcherQueue.TryEnqueue(() => UpdateProgress(job));
    }

    private void UpdateProgress(DownloadJob job)
    {
        _job = job;
        ProgressFileName.Text = job.FileName;

        var progress = job.Progress;
        ProgressRibbon.Segments = progress?.Segments is { Count: > 0 } segs ? segs : null;
        ProgressRibbon.Fraction = progress?.Fraction ?? 0;
        ProgressRibbon.Tone = job.State switch
        {
            DownloadState.Completed => "completed",
            DownloadState.Failed or DownloadState.Retrying => "failed",
            DownloadState.Paused => "paused",
            _ => "running",
        };

        ProgressSize.Text = progress is null
            ? "—"
            : progress.TotalBytes is > 0
                ? $"{DownloadRowViewModel.Bytes(progress.CompletedBytes)} / {DownloadRowViewModel.Bytes(progress.TotalBytes.Value)}"
                : DownloadRowViewModel.Bytes(progress.CompletedBytes);

        var running = job.State == DownloadState.Running;
        ProgressSpeed.Text = running && progress is { BytesPerSecond: > 1 }
            ? $"{DownloadRowViewModel.Bytes((long)progress.BytesPerSecond)}/s"
            : "";
        ProgressRemaining.Text = running && progress?.Remaining is { } left
            ? DownloadRowViewModel.Duration(left)
            : "";

        var origin = job.Request.Uri.Scheme == TransferRouting.MagnetScheme
            ? "BitTorrent"
            : job.Request.Uri.Host;

        ProgressStatus.Text = job.State switch
        {
            DownloadState.Queued => "排隊中",
            DownloadState.Running => progress?.Note is { Length: > 0 } note
                ? note
                : job.Attempt > 1
                    ? $"{origin}（第 {job.Attempt} 次嘗試）"
                    : origin,
            DownloadState.Retrying => $"{job.Error ?? "連線中斷"} 稍後自動重試",
            DownloadState.Paused => "已暫停",
            DownloadState.Completed => "已完成",
            DownloadState.Failed => job.Error ?? "下載失敗",
            _ => "",
        };

        if (job.State == DownloadState.Completed)
        {
            Title = $"{job.FileName} - 下載完成";
            RunningActions.Visibility = Visibility.Collapsed;
            CompletedActions.Visibility = Visibility.Visible;
            FailedActions.Visibility = Visibility.Collapsed;
            OpenFileButton.Focus(FocusState.Programmatic);
        }
        else if (job.State == DownloadState.Failed)
        {
            Title = $"{job.FileName} - 下載失敗";
            RunningActions.Visibility = Visibility.Collapsed;
            CompletedActions.Visibility = Visibility.Collapsed;
            FailedActions.Visibility = Visibility.Visible;
        }
        else
        {
            RunningActions.Visibility = Visibility.Visible;
            CompletedActions.Visibility = Visibility.Collapsed;
            FailedActions.Visibility = Visibility.Collapsed;
            PauseResumeButton.Content = job.State == DownloadState.Paused ? "繼續" : "暫停";
        }
    }

    private void PauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        if (_job.State == DownloadState.Running)
        {
            App.Queue.Pause(_job.Id);
        }
        else if (_job.State is DownloadState.Paused or DownloadState.Failed)
        {
            App.Queue.Resume(_job.Id);
        }
    }

    private void CancelDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_job is not null)
        {
            App.Queue.Remove(_job.Id);
        }
        Close();
    }

    private void OpenFileClick(object sender, RoutedEventArgs e)
    {
        if (_job?.Path is { } path && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }
        Close();
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_job?.Path is { } path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void CloseCompleteClick(object sender, RoutedEventArgs e) => Close();

    private void RetryClick(object sender, RoutedEventArgs e)
    {
        if (_job is not null)
        {
            App.Queue.Resume(_job.Id);
        }
    }

    private void LaterClick(object sender, RoutedEventArgs e)
    {
        if (_settled) return;
        _settled = true;
        Interlocked.Decrement(ref _open);

        _stopAsking?.Invoke(StopAsking.IsChecked == true);

        var name = FileName.Text.Trim();
        _accepted(
            _capture.Request with
            {
                FileName = _capture.Request.Kind == TransferKind.Http && name.Length > 0 ? name : null,
                Directory = Folder.Text,
                SortIntoCategories = SortIntoCategories.IsChecked == true,
            },
            false);

        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private void Settle(bool? start)
    {
        if (_settled) return;
        _settled = true;

        Interlocked.Decrement(ref _open);

        if (start is not { } shouldStart) return;

        _stopAsking?.Invoke(StopAsking.IsChecked == true);

        var name = FileName.Text.Trim();
        _accepted(
            _capture.Request with
            {
                FileName = _capture.Request.Kind == TransferKind.Http && name.Length > 0 ? name : null,
                Directory = Folder.Text,
                SortIntoCategories = SortIntoCategories.IsChecked == true,
            },
            shouldStart);

        Close();
    }
}
