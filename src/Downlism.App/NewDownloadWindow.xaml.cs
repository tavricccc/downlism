using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Downlism.App.Services;
using Downlism.App.ViewModels;
using Downlism.Core.Downloads;
using Downlism.Core.Http;
using Downlism.Core.Media;
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
    private readonly CancellationTokenSource _mediaProbeCancellation = new();
    private bool _settled;
    private MediaFormats? _mediaFormats;
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

        App.ApplyTheme(Root, App.ThemePreference);
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
            presenter.IsAlwaysOnTop = App.CurrentSettings.PromptAlwaysOnTop;
        }

        Fill();
        StopAsking.Visibility = stopAsking is null ? Visibility.Collapsed : Visibility.Visible;

        Root.Loaded += OnLoaded;

        Closed += (_, _) =>
        {
            _mediaProbeCancellation.Cancel();
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

        var mediaVisibility = request.Kind == TransferKind.Media ? Visibility.Visible : Visibility.Collapsed;
        MediaTypeLabel.Visibility = mediaVisibility;
        MediaType.Visibility = mediaVisibility;
        MediaQualityLabel.Visibility = mediaVisibility;
        MediaQualityPanel.Visibility = mediaVisibility;
        if (request.Kind == TransferKind.Media)
        {
            MediaType.SelectedIndex = request.MediaOutput == MediaOutput.Audio ? 1 : 0;
            MediaType.IsEnabled = false;
            MediaQuality.IsEnabled = false;
            LaterButton.IsEnabled = false;
            StartButton.IsEnabled = false;
            MediaQuality.Items.Clear();
            MediaQuality.Items.Add(new ComboBoxItem { Content = "正在讀取…" });
            MediaQuality.SelectedIndex = 0;
            MediaStatus.Text = "正在向來源查詢可下載的格式";
        }

        Folder.Text = request.Directory;
        SortIntoCategories.IsChecked = request.SortIntoCategories;
        JobConnections.Value = request.Connections;
        JobSpeed.Value = (double)request.BytesPerSecond / 1024;
        JobConnections.IsEnabled = request.Kind != TransferKind.Torrent;
        JobSpeed.IsEnabled = request.Kind != TransferKind.Torrent;
        ExpectedHash.IsEnabled = request.Kind == TransferKind.Http;
        ExpectedHash.Text = request.ExpectedSha256 ?? "";
    }

    private static string Describe(TransferKind kind, long bytes) => kind switch
    {
        TransferKind.Media => "由 yt-dlp 解析來源後才會知道",
        TransferKind.Torrent => "取得種子資訊後才會知道",
        _ => bytes > 0 ? DownloadRowViewModel.Bytes(bytes) : "開始下載後才會知道",
    };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToContent();
        if (_capture.Request.Kind != TransferKind.Media) return;

        try
        {
            _mediaFormats = await App.Queue.ProbeMediaAsync(
                _capture.Request,
                status => DispatcherQueue.TryEnqueue(() => MediaStatus.Text = status),
                _mediaProbeCancellation.Token);

            VideoOption.IsEnabled = _mediaFormats.HasVideo || (!_mediaFormats.HasVideo && !_mediaFormats.HasAudio);
            AudioOption.IsEnabled = _mediaFormats.HasAudio || (!_mediaFormats.HasVideo && !_mediaFormats.HasAudio);

            if (!_mediaFormats.HasVideo && _mediaFormats.HasAudio) MediaType.SelectedIndex = 1;
            else if (_mediaFormats.HasVideo && !_mediaFormats.HasAudio) MediaType.SelectedIndex = 0;

            var output = SelectedMediaOutput();
            FillMediaQualities(output, _capture.Request.MediaQuality);
            MediaStatus.Text = DescribeFormats(_mediaFormats);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            // The download can still succeed: format sorting below asks yt-dlp for the nearest
            // available stream rather than requiring one exact format ID.
            _mediaFormats = null;
            VideoOption.IsEnabled = true;
            AudioOption.IsEnabled = true;
            FillMediaQualities(SelectedMediaOutput(), _capture.Request.MediaQuality);
            MediaStatus.Text = $"無法讀取品質：{exception.Message}。下載時會自動選最接近的格式。";
        }

        MediaType.IsEnabled = true;
        MediaQuality.IsEnabled = true;
        LaterButton.IsEnabled = true;
        StartButton.IsEnabled = true;
        FitToContent();
    }

    private void MediaTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaType.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<MediaOutput>(tag, out var output)) return;

        KindLabel.Text = output == MediaOutput.Audio ? "音訊" : "影片";
        if (MediaType.IsEnabled) FillMediaQualities(output, selected: null);
    }

    private MediaOutput SelectedMediaOutput() =>
        MediaType.SelectedItem is ComboBoxItem { Tag: string tag }
        && Enum.TryParse<MediaOutput>(tag, out var output)
            ? output
            : MediaOutput.Video;

    private void FillMediaQualities(MediaOutput output, int? selected)
    {
        var values = output == MediaOutput.Audio
            ? _mediaFormats?.AudioBitrates ?? [320, 256, 192, 128]
            : _mediaFormats?.VideoHeights ?? [2160, 1440, 1080, 720, 480, 360];
        var suffix = output == MediaOutput.Audio ? " kbps" : "p";
        var best = output == MediaOutput.Audio ? "最佳品質" : "最佳畫質";

        MediaQuality.Items.Clear();
        MediaQuality.Items.Add(new ComboBoxItem { Content = best, Tag = string.Empty });
        foreach (var value in values)
        {
            MediaQuality.Items.Add(new ComboBoxItem
            {
                Content = value.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix,
                Tag = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        MediaQuality.SelectedIndex = 0;
        if (selected is not > 0) return;

        for (var index = 1; index < MediaQuality.Items.Count; index++)
        {
            if (MediaQuality.Items[index] is ComboBoxItem { Tag: string tag }
                && int.TryParse(tag, out var value)
                && value == selected)
            {
                MediaQuality.SelectedIndex = index;
                break;
            }
        }
    }

    private static string DescribeFormats(MediaFormats formats)
    {
        var parts = new List<string>();
        if (formats.VideoHeights.Count > 0) parts.Add($"{formats.VideoHeights.Count} 種影片畫質");
        if (formats.AudioBitrates.Count > 0) parts.Add($"{formats.AudioBitrates.Count} 種音訊品質");
        return parts.Count > 0 ? "已讀取「" + string.Join("、", parts) + "」" : "來源未提供品質明細；下載時會自動選擇。";
    }

    private DownloadRequest RequestFromPrompt()
    {
        var request = _capture.Request;
        var name = FileName.Text.Trim();
        var output = request.MediaOutput;
        var quality = request.MediaQuality;

        if (request.Kind == TransferKind.Media
            && MediaType.SelectedItem is ComboBoxItem { Tag: string outputTag }
            && Enum.TryParse<MediaOutput>(outputTag, out var selectedOutput))
        {
            output = selectedOutput;
            quality = MediaQuality.SelectedItem is ComboBoxItem { Tag: string qualityTag }
                && int.TryParse(qualityTag, out var selectedQuality)
                    ? selectedQuality
                    : null;
        }

        if (!double.IsFinite(JobConnections.Value) || JobConnections.Value != Math.Truncate(JobConnections.Value) ||
            !double.IsFinite(JobSpeed.Value)) throw new ArgumentException("請輸入有效的連線數與限速。");
        var hash = ExpectedHash.Text.Trim();
        if (request.Kind == TransferKind.Http && hash.Length > 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new ArgumentException("SHA-256 必須是 64 位十六進位字元。");
        return request with
        {
            FileName = request.Kind == TransferKind.Http && name.Length > 0 &&
                (request.FileName is not null || name != SuggestedFileName.FromUri(request.Uri)) ? name : null,
            Connections = Math.Clamp((int)JobConnections.Value, 1, 32),
            BytesPerSecond = Math.Clamp((long)Math.Round(JobSpeed.Value * 1024), 0, 10L * 1024 * 1024 * 1024),
            ExpectedSha256 = request.Kind == TransferKind.Http && hash.Length > 0 ? hash : null,
            Directory = Folder.Text,
            SortIntoCategories = SortIntoCategories.IsChecked == true,
            MediaOutput = output,
            MediaQuality = quality,
        };
    }

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) Folder.Text = folder.Path;
        }
        catch (Exception ex) { ShowPromptError("無法開啟資料夾選擇器：" + ex.Message); }
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        if (_settled) return;
        DownloadRequest request;
        try { request = RequestFromPrompt(); }
        catch (Exception ex) { ShowPromptError(ex.Message); return; }
        _settled = true;

        Interlocked.Decrement(ref _open);

        _stopAsking?.Invoke(StopAsking.IsChecked == true);

        // User operated the prompt: unpin topmost and allow minimize during downloading!
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsMinimizable = true;
        }

        _job = _accepted(request, true);
        if (!App.CurrentSettings.KeepProgressWindow) { Close(); return; }

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
        if (job.State == DownloadState.Removed) { Close(); return; }
        ProgressFileName.Text = job.FileName;
        ProgressFolder.Text = $"存到：{job.Request.Directory}";

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
        if (_job.State is DownloadState.Running or DownloadState.Queued or DownloadState.Retrying)
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
        DownloadRequest request;
        try { request = RequestFromPrompt(); }
        catch (Exception ex) { ShowPromptError(ex.Message); return; }
        _settled = true;
        Interlocked.Decrement(ref _open);

        _stopAsking?.Invoke(StopAsking.IsChecked == true);

        _accepted(request, false);

        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void ShowPromptError(string message)
    {
        PromptError.Text = message;
        PromptError.Visibility = Visibility.Visible;
        FitToContent();
    }

    private void Settle(bool? start)
    {
        if (_settled) return;
        _settled = true;

        Interlocked.Decrement(ref _open);

        if (start is not { } shouldStart) return;

        _stopAsking?.Invoke(StopAsking.IsChecked == true);

        _accepted(RequestFromPrompt(), shouldStart);

        Close();
    }
}
