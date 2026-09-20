using System.Collections.ObjectModel;
using System.Diagnostics;
using Downlism.App.Services;
using Downlism.App.ViewModels;
using Downlism.Core.Downloads;
using Downlism.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Downlism.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadRowViewModel> _rows = [];
    private readonly ObservableCollection<DownloadRowViewModel> _visibleRows = [];
    private readonly Dictionary<Guid, DownloadRowViewModel> _byId = [];
    private readonly DispatcherQueue _dispatcher;
    private readonly DownloadStore _store = new(DownloadStore.DefaultPath);
    private readonly LoginStartupService _loginStartup = new();
    private AppSettings _settings = AppSettings.Load();
    private string? _lastClipboardUrl;
    private long _bytesPerSecond;

    /// <summary>
    /// False until the settings controls have been filled in. Selecting an item raises
    /// SelectionChanged, and without this the constructor would write every setting straight
    /// back out again before the window is even shown.
    /// </summary>
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Downloads.ItemsSource = _visibleRows;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 660));
        AppWindow.SetIcon("Assets/Downlism.ico");

        App.Queue.Changed += OnJobChanged;
        Closed += (_, _) =>
        {
            App.Queue.Changed -= OnJobChanged;
            Clipboard.ContentChanged -= OnClipboardChanged;
        };

        _bytesPerSecond = _settings.BytesPerSecond;
        // Read from the Run key rather than from settings.json: the registry is where the
        // choice actually lives, and a copy in the settings file would disagree with it the
        // first time the value is removed from outside the app.
        LaunchAtLogin.IsChecked = _loginStartup.IsEnabled();
        SortIntoCategories.IsChecked = _settings.SortIntoCategories;
        WatchClipboard.IsChecked = _settings.WatchClipboard;
        AskOnCapture.IsChecked = _settings.PromptOnCapture;

        Select(Connections, _settings.Connections);
        Select(ConcurrentDownloads, _settings.ConcurrentDownloads);
        Select(SpeedLimit, _settings.BytesPerSecond);
        _ready = true;

        App.Queue.Retry = new RetryPolicy(Math.Max(1, _settings.RetryAttempts));
        // Applied here rather than at the queue's construction, which happens before any
        // settings have been read. Without this the stored limit was written, shown and ignored.
        App.Queue.SetConcurrency(Math.Clamp(_settings.ConcurrentDownloads, 1, 16));
        _ = App.Queue.SetTorrentRateLimitAsync(_settings.BytesPerSecond);
        if (_settings.WatchClipboard) Clipboard.ContentChanged += OnClipboardChanged;

        RestoreHistory();
        RefreshDownloadView();
    }

    /// <summary>
    /// Brings back the list from the previous session. Nothing is restarted automatically: an
    /// app that reopens and immediately saturates the connection is a nuisance, and the partial
    /// files are still on disk, so resuming is one click away.
    /// </summary>
    private void RestoreHistory()
    {
        foreach (var stored in _store.Load())
        {
            if (!TransferRouting.TryParse(stored.Url, out var uri)) continue;

            var job = App.Queue.Restore(
                stored.Id,
                new DownloadRequest
                {
                    Uri = uri,
                    Kind = stored.Kind,
                    PageUrl = stored.PageUrl,
                    Directory = stored.Directory,
                    FileName = stored.FileName,
                    // Already the final directory; sorting again would nest a second folder.
                    SortIntoCategories = false,
                    Referrer = stored.Referrer,
                },
                stored.State == "Completed" ? DownloadState.Completed : DownloadState.Paused,
                stored.Path);

            Track(job, remember: false);
        }
    }

    /// <summary>Called by the ingest listener from a background thread.</summary>
    public void AddFromBrowser(CaptureRequest capture) => _dispatcher.TryEnqueue(() => Capture(capture));

    /// <summary>
    /// Opens the per-download window for a capture, or starts it outright if the person has
    /// said they no longer want to be asked.
    /// </summary>
    /// <remarks>
    /// Raising the whole list for every captured download would put a thousand-pixel window
    /// over whatever they were reading in order to say one sentence. The prompt is the small
    /// version of that, and it is also the only moment where the name and the folder can still
    /// be changed without moving a finished file afterwards.
    /// </remarks>
    private void Capture(CaptureRequest capture)
    {
        if (!_settings.PromptOnCapture)
        {
            Track(App.Queue.Add(capture.Request));
            return;
        }

        var window = new NewDownloadWindow(capture, Accept, StopAsking);
        window.Activate();
        window.ForceForeground();
    }

    /// <summary>
    /// Takes the request back from the prompt. "Later" enters the row without starting it,
    /// which is the same state a paused transfer is in, so the resume button already works.
    /// </summary>
    private DownloadJob Accept(DownloadRequest request, bool start)
    {
        RememberDownloadDefaults(request);

        var job = start
            ? App.Queue.Add(request)
            : App.Queue.Restore(Guid.NewGuid(), request, DownloadState.Paused, null);
        Track(job);
        return job;
    }

    private void StopAsking(bool stop)
    {
        if (!stop) return;

        _settings = _settings with { PromptOnCapture = false };
        _settings.Save();
        AskOnCapture.IsChecked = false;
    }

    /// <summary>
    /// The choices made in the prompt become the defaults for the next one. They are only
    /// remembered after Start or Later, so cancelling a prompt never changes preferences.
    /// </summary>
    private void RememberDownloadDefaults(DownloadRequest request)
    {
        var updated = _settings;
        if (!string.IsNullOrWhiteSpace(request.Directory) && request.Directory != updated.DownloadFolder)
        {
            updated = updated with { DownloadFolder = request.Directory };
        }

        if (request.SortIntoCategories != updated.SortIntoCategories)
        {
            updated = updated with { SortIntoCategories = request.SortIntoCategories };
            SortIntoCategories.IsChecked = request.SortIntoCategories;
        }

        if (updated == _settings) return;

        _settings = updated;
        _settings.Save();
    }

    private void OnJobChanged(DownloadJob job) => _dispatcher.TryEnqueue(() =>
    {
        if (!_byId.TryGetValue(job.Id, out var row)) return;

        if (job.State == DownloadState.Removed)
        {
            _rows.Remove(row);
            _byId.Remove(job.Id);
            _store.Delete(job.Id);
            RefreshDownloadView();
            return;
        }

        row.Refresh();
        RefreshDownloadView();

        // Only settled states are written back. Persisting every progress sample would put a
        // database write on a path that fires four times a second per transfer.
        if (job.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Paused)
        {
            Remember(job);
        }
    });

    private void Track(DownloadJob job, bool remember = true)
    {
        var row = new DownloadRowViewModel(job);
        row.Refresh();

        // Newest first: the transfer a person just started is the one they want to watch.
        _rows.Insert(0, row);
        _byId[job.Id] = row;

        if (remember) Remember(job);
        RefreshDownloadView();
    }

    private void Remember(DownloadJob job) => _store.Save(new StoredDownload(
        job.Id,
        job.Request.Uri.AbsoluteUri,
        job.Request.Directory,
        job.FileName,
        job.Request.Referrer,
        job.State.ToString(),
        job.Path,
        DateTimeOffset.UtcNow,
        job.Request.Kind,
        job.Request.PageUrl));

    private void RefreshDownloadView()
    {
        var filter = SelectedFilter();
        var wanted = _rows.Where(row => MatchesFilter(row.Job.State, filter)).ToArray();

        // Progress updates arrive several times a second. Rebuild only when a row crosses a
        // filter boundary, otherwise the list would lose hover/focus state while bytes move.
        if (!_visibleRows.SequenceEqual(wanted))
        {
            _visibleRows.Clear();
            foreach (var row in wanted) _visibleRows.Add(row);
        }

        var active = _rows.Count(row => IsActive(row.Job.State));
        var completed = _rows.Count(row => row.Job.State == DownloadState.Completed);
        var speed = _rows
            .Where(row => row.Job.State == DownloadState.Running)
            .Sum(row => row.Job.Progress?.BytesPerSecond ?? 0);

        Summary.Text = active > 0
            ? $"{active} 個進行中 · {DownloadRowViewModel.Bytes((long)speed)}/s · {_rows.Count} 個下載"
            : $"{_rows.Count} 個下載";
        PauseAllButton.IsEnabled = active > 0;
        ClearCompletedButton.IsEnabled = completed > 0;

        var empty = _visibleRows.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Downloads.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        (EmptyTitle.Text, EmptyDescription.Text) = _rows.Count == 0
            ? ("還沒有下載", "貼上下載連結、影片頁面網址或磁力連結，或從瀏覽器擴充功能接手下載。")
            : filter switch
            {
                "active" => ("目前沒有進行中的下載", "開始新的下載，或到「需要處理」繼續已暫停的項目。"),
                "completed" => ("還沒有完成的下載", "下載完成後會集中顯示在這裡。"),
                "attention" => ("目前沒有需要處理的下載", "已暫停或失敗的項目會顯示在這裡。"),
                _ => ("這個檢視沒有下載", "切換篩選條件即可查看其他下載。"),
            };
    }

    private string SelectedFilter() =>
        StatusFilter.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "all";

    private static bool MatchesFilter(DownloadState state, string filter) => filter switch
    {
        "active" => IsActive(state),
        "completed" => state == DownloadState.Completed,
        "attention" => state is DownloadState.Paused or DownloadState.Failed,
        _ => state != DownloadState.Removed,
    };

    private static bool IsActive(DownloadState state) =>
        state is DownloadState.Queued or DownloadState.Running or DownloadState.Retrying;

    private void StatusFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) RefreshDownloadView();
    }

    private void ClearCompletedClick(object sender, RoutedEventArgs e)
    {
        foreach (var id in _rows
                     .Where(row => row.Job.State == DownloadState.Completed)
                     .Select(row => row.Id)
                     .ToArray())
        {
            App.Queue.Remove(id);
        }
    }

    private async void PasteClick(object sender, RoutedEventArgs e)
    {
        if (await ClipboardLinkAsync() is not { } uri)
        {
            // Say what is wrong and what to do, not that something failed.
            Show("剪貼簿裡沒有網址。複製下載連結、影片頁面網址或磁力連結後再試一次。", InfoBarSeverity.Informational);
            return;
        }

        Start(uri, TransferRouting.For(uri));
    }

    /// <summary>
    /// Sends whatever is on the clipboard to yt-dlp, whatever the URL looks like. The routing
    /// rules only recognise manifests and a short list of well-known sites; this is how to say
    /// "there is a video on this page" about the thousand sites they do not list.
    /// </summary>
    private async void PasteMediaClick(object sender, RoutedEventArgs e)
    {
        if (await ClipboardLinkAsync() is not { } uri)
        {
            Show("剪貼簿裡沒有網址。複製影片頁面的網址後再試一次。", InfoBarSeverity.Informational);
            return;
        }

        if (uri.Scheme == TransferRouting.MagnetScheme)
        {
            Show("磁力連結不是影片頁面，直接按「貼上網址」即可。", InfoBarSeverity.Informational);
            return;
        }

        if (!App.Queue.MediaToolsReady)
        {
            // Said before the wait rather than during it: a first run spends several minutes
            // fetching yt-dlp and ffmpeg, and an unexplained pause reads as a hang.
            Show("第一次下載影片會先取得 yt-dlp 與 ffmpeg，約 200 MB，之後不會再下載一次。", InfoBarSeverity.Informational);
        }

        Start(uri, TransferKind.Media);
    }

    private async Task<Uri?> ClipboardLinkAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return null;

        var text = (await content.GetTextAsync()).Trim();
        return TransferRouting.TryParse(text, out var uri) ? uri : null;
    }

    /// <summary>
    /// Offers a link in the per-download window rather than starting it. The folder still
    /// defaults to a category of the Downloads folder, so the usual undifferentiated pile is
    /// avoided without anyone having to choose anything.
    /// </summary>
    private void Start(Uri uri, TransferKind kind)
    {
        var request = new DownloadRequest
        {
            Uri = uri,
            Kind = kind,
            // For media the URL is both the target and the page yt-dlp resolves. The other two
            // engines have no page involved at all.
            PageUrl = kind == TransferKind.Media ? uri.AbsoluteUri : null,
            Directory = IngestListener.DownloadFolder(_settings),
            SortIntoCategories = _settings.SortIntoCategories,
            Connections = _settings.Connections,
            BytesPerSecond = _bytesPerSecond,
        };

        // A link added by hand always asks, whatever the capture setting says: the person is
        // already here, and this is where the name and the folder get decided.
        var window = new NewDownloadWindow(new CaptureRequest(request, 0), Accept);
        window.Activate();
        window.ForceForeground();
    }

    /// <summary>Read by the ingest listener so browser handovers follow the same choices.</summary>
    public AppSettings Settings => _settings;

    private void WatchClipboardClick(object sender, RoutedEventArgs e)
    {
        var watch = WatchClipboard.IsChecked == true;
        _settings = _settings with { WatchClipboard = watch };
        _settings.Save();

        Clipboard.ContentChanged -= OnClipboardChanged;
        if (watch) Clipboard.ContentChanged += OnClipboardChanged;
    }

    /// <summary>
    /// Picks the item whose Tag is this number, falling back to the first. The tags are the
    /// values themselves, so the stored setting survives the list being reordered or extended.
    /// </summary>
    private static void Select(ComboBox box, long value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is not string tag || !long.TryParse(tag, out var candidate) || candidate != value) continue;

            box.SelectedItem = item;
            return;
        }

        box.SelectedIndex = 0;
    }

    /// <summary>Reads the selected Tag, or null when the box holds something unexpected.</summary>
    private static long? SelectedValue(ComboBox box) =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } && long.TryParse(tag, out var value) ? value : null;

    private void ConnectionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SelectedValue(Connections) is not { } value) return;

        // Applies to transfers started from here on. Re-planning the segments of one already
        // running would mean discarding the ranges it has partly filled.
        _settings = _settings with { Connections = (int)value };
        _settings.Save();
    }

    private void ConcurrentDownloadsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SelectedValue(ConcurrentDownloads) is not { } value) return;

        _settings = _settings with { ConcurrentDownloads = (int)value };
        _settings.Save();
        App.Queue.SetConcurrency((int)value);
    }

    /// <summary>Shows the four steps that load the browser extension.</summary>
    private void ExtensionGuideClick(object sender, RoutedEventArgs e) => ShowExtensionGuide();

    public void ShowExtensionGuide() => new ExtensionGuideWindow().Activate();

    /// <summary>
    /// Shows the extension guide once this window has drawn, for the launch that follows an
    /// install. Must be called before the window is activated.
    /// </summary>
    /// <remarks>
    /// Hung off this window's own Loaded rather than off Activated, which has usually already
    /// fired by the time the app has anything to subscribe to, and then queued at low priority
    /// so the guide opens onto a list that has finished its first layout instead of racing it.
    /// </remarks>
    public void ShowExtensionGuideOnFirstFrame()
    {
        Root.Loaded += OnFirstLoad;

        void OnFirstLoad(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OnFirstLoad;
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, ShowExtensionGuide);
        }
    }

    private void AskOnCaptureClick(object sender, RoutedEventArgs e)
    {
        // The way back. Without a control here, the checkbox in the prompt would be a one-way
        // door out of a feature.
        _settings = _settings with { PromptOnCapture = AskOnCapture.IsChecked == true };
        _settings.Save();
    }

    /// <summary>
    /// Raised after the window changes the login-startup setting, so the tray menu's tick does
    /// not go on claiming the opposite of what the settings flyout now shows.
    /// </summary>
    public event EventHandler? LaunchAtLoginChanged;

    /// <summary>Re-reads the Run key, for when the tray menu was the one that changed it.</summary>
    public void RefreshLaunchAtLogin() => LaunchAtLogin.IsChecked = _loginStartup.IsEnabled();

    private void LaunchAtLoginClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _loginStartup.SetEnabled(LaunchAtLogin.IsChecked == true);
            LaunchAtLoginChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            // Nothing was written, so the checkbox must not keep the state it was clicked into.
            LaunchAtLogin.IsChecked = _loginStartup.IsEnabled();
        }
    }

    private void SortIntoCategoriesClick(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { SortIntoCategories = SortIntoCategories.IsChecked == true };
        _settings.Save();
    }

    /// <summary>
    /// Offers a copied link rather than starting it. Downloading whatever lands on the
    /// clipboard would be a trap: people copy links to read them, to share them, to search
    /// them. Asking costs one click and is never wrong.
    /// </summary>
    private async void OnClipboardChanged(object? sender, object e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;

            var text = (await content.GetTextAsync()).Trim();
            if (text == _lastClipboardUrl) return;
            if (!TransferRouting.TryParse(text, out var uri)) return;

            _lastClipboardUrl = text;
            Offer(uri);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
        {
            // Another application was holding the clipboard; the next copy will work.
        }
    }

    private void Offer(Uri uri)
    {
        var kind = TransferRouting.For(uri);

        var action = new Button { Content = "下載" };
        action.Click += (_, _) =>
        {
            Notice.IsOpen = false;
            Start(uri, kind);
        };

        Notice.ActionButton = action;

        var what = kind switch
        {
            TransferKind.Torrent => $"磁力連結 {Downlism.Core.Http.SuggestedFileName.FromUri(uri)}",
            TransferKind.Media => $"影片來源 {uri.Host}",
            _ => Downlism.Core.Http.SuggestedFileName.FromUri(uri),
        };

        Show($"剪貼簿裡有 {what}", InfoBarSeverity.Informational);
    }

    private async void HashClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || !_byId.TryGetValue(id, out var row)) return;
        if (row.Job.Path is not { } path || !File.Exists(path)) return;

        Notice.ActionButton = null;
        Show("正在計算 SHA-256…", InfoBarSeverity.Informational);

        try
        {
            var hash = await FileHash.ComputeAsync(path, FileHash.Algorithm.Sha256);

            // Copied rather than shown alone: the next thing anyone does with a checksum is
            // compare it with one on a web page.
            var package = new DataPackage();
            package.SetText(hash);
            Clipboard.SetContent(package);

            Show($"SHA-256 已複製：{hash}", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Show("無法讀取檔案來計算雜湊。", InfoBarSeverity.Error);
        }
    }

    private void PauseAllClick(object sender, RoutedEventArgs e) => App.Queue.PauseAll();

    private void PauseClick(object sender, RoutedEventArgs e) => WithId(sender, App.Queue.Pause);

    private void ResumeClick(object sender, RoutedEventArgs e) => WithId(sender, App.Queue.Resume);

    private void RemoveClick(object sender, RoutedEventArgs e) => WithId(sender, App.Queue.Remove);

    private void OpenFolderClick(object sender, RoutedEventArgs e) => WithId(sender, id =>
    {
        if (!_byId.TryGetValue(id, out var row) || row.Job.Path is not { } path) return;

        // Selecting the file is more useful than opening the folder and leaving the person to
        // find it among hundreds of others.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    });

    private void SpeedLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SelectedValue(SpeedLimit) is not { } rate) return;

        // Applies to transfers started from here on; changing the ceiling mid-flight would mean
        // tearing down connections that are already moving bytes.
        _bytesPerSecond = rate;
        _settings = _settings with { BytesPerSecond = rate };
        _settings.Save();

        // BitTorrent throttles per session rather than per transfer, so the new ceiling has to
        // reach torrents that are already running.
        _ = App.Queue.SetTorrentRateLimitAsync(rate);
    }

    private static void WithId(object sender, Action<Guid> action)
    {
        if (sender is FrameworkElement { Tag: Guid id }) action(id);
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}
