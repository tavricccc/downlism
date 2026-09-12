using System.Collections.ObjectModel;
using System.Diagnostics;
using Downlism.App.Services;
using Downlism.App.ViewModels;
using Downlism.Core.Downloads;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Downlism.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadRowViewModel> _rows = [];
    private readonly Dictionary<Guid, DownloadRowViewModel> _byId = [];
    private readonly DispatcherQueue _dispatcher;
    private readonly DownloadStore _store = new(DownloadStore.DefaultPath);
    private long _bytesPerSecond;

    public MainWindow()
    {
        InitializeComponent();

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Downloads.ItemsSource = _rows;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 660));
        AppWindow.SetIcon("Assets/Downlism.ico");

        App.Queue.Changed += OnJobChanged;
        Closed += (_, _) => App.Queue.Changed -= OnJobChanged;

        RestoreHistory();
        UpdateEmptyState();
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
            if (!Uri.TryCreate(stored.Url, UriKind.Absolute, out var uri)) continue;

            var job = App.Queue.Restore(
                stored.Id,
                new DownloadRequest
                {
                    Uri = uri,
                    Directory = stored.Directory,
                    FileName = stored.FileName,
                    Referrer = stored.Referrer,
                },
                stored.State == "Completed" ? DownloadState.Completed : DownloadState.Paused,
                stored.Path);

            Track(job, remember: false);
        }
    }

    /// <summary>Called by the ingest listener from a background thread.</summary>
    public void AddFromBrowser(DownloadJob job) => _dispatcher.TryEnqueue(() => Track(job));

    private void OnJobChanged(DownloadJob job) => _dispatcher.TryEnqueue(() =>
    {
        if (!_byId.TryGetValue(job.Id, out var row)) return;

        if (job.State == DownloadState.Removed)
        {
            _rows.Remove(row);
            _byId.Remove(job.Id);
            _store.Delete(job.Id);
            UpdateEmptyState();
            return;
        }

        row.Refresh();

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
        UpdateEmptyState();
    }

    private void Remember(DownloadJob job) => _store.Save(new StoredDownload(
        job.Id,
        job.Request.Uri.AbsoluteUri,
        job.Request.Directory,
        job.FileName,
        job.Request.Referrer,
        job.State.ToString(),
        job.Path,
        DateTimeOffset.UtcNow));

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Downloads.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void PasteClick(object sender, RoutedEventArgs e)
    {
        var content = Clipboard.GetContent();
        var text = content.Contains(StandardDataFormats.Text) ? (await content.GetTextAsync()).Trim() : string.Empty;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Say what is wrong and what to do, not that something failed.
            Show("剪貼簿裡沒有 http 或 https 網址。複製一個下載連結後再試一次。", InfoBarSeverity.Informational);
            return;
        }

        Track(App.Queue.Add(new DownloadRequest
        {
            Uri = uri,
            Directory = IngestListener.DownloadFolder(),
            BytesPerSecond = _bytesPerSecond,
        }));
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
        if (SpeedLimit.SelectedItem is not ComboBoxItem { Tag: string tag } || !long.TryParse(tag, out var rate)) return;

        // Applies to transfers started from here on; changing the ceiling mid-flight would mean
        // tearing down connections that are already moving bytes.
        _bytesPerSecond = rate;
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
