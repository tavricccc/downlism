using System.Collections.Concurrent;
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
using Windows.Storage.Pickers;

namespace Downlism.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadRowViewModel> _rows = [];
    private readonly ObservableCollection<DownloadRowViewModel> _visibleRows = [];
    private readonly Dictionary<Guid, DownloadRowViewModel> _byId = [];
    private readonly Dictionary<Guid, (DownloadRequest Request, DownloadState State)> _persisted = [];
    private readonly ConcurrentDictionary<Guid, DownloadJob> _changes = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _refresh;
    private readonly DownloadStore _store = new(DownloadStore.DefaultPath);
    private AppSettings _settings = AppSettings.Load();
    private SettingsWindow? _settingsWindow;
    private string? _lastClipboardUrl;
    private bool _ready;
    private bool _dialogOpen;
    private bool _clipboardPromptOpen;
    private readonly WindowDialogs _dialogs;

    public MainWindow()
    {
        InitializeComponent();
        _dialogs = new(this, Root);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Downloads.ItemsSource = _visibleRows;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 720));
        AppWindow.SetIcon("Assets/Downlism.ico");
        App.ApplyTheme(Root, _settings.Theme);
        App.Queue.Changed += OnJobChanged;
        _refresh = _dispatcher.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(250);
        _refresh.Tick += (_, _) => FlushChanges();
        _refresh.Start();
        Closed += (_, _) =>
        {
            _refresh.Stop(); App.Queue.Changed -= OnJobChanged;
            Clipboard.ContentChanged -= OnClipboardChanged;
            _settingsWindow?.Close();
        };
        ConfigureQueue();
        if (_settings.WatchClipboard) Clipboard.ContentChanged += OnClipboardChanged;
        RestoreHistory();
        _ready = true;
        RefreshDownloadView();
    }

    public AppSettings Settings => _settings;
    public event EventHandler? LaunchAtLoginChanged;
    public void RefreshLaunchAtLogin() { }

    private void ConfigureQueue()
    {
        App.Queue.Retry = new RetryPolicy(_settings.RetryAttempts, _settings.RetryDelaySeconds);
        App.Queue.SetConcurrency(_settings.ConcurrentDownloads);
        _ = ApplyTorrentRateAsync();
    }
    private async Task ApplyTorrentRateAsync()
    {
        try { await App.Queue.SetTorrentRateLimitAsync(_settings.BytesPerSecond); }
        catch (Exception ex) { Show("BitTorrent 限速未套用：" + ex.Message, InfoBarSeverity.Warning); }
    }
    private void ApplySettings(AppSettings settings)
    {
        settings.Save();
        _settings = settings;
        App.ApplyTheme(Root, settings.Theme);
        Clipboard.ContentChanged -= OnClipboardChanged;
        if (settings.WatchClipboard) Clipboard.ContentChanged += OnClipboardChanged;
        ConfigureQueue();
        LaunchAtLoginChanged?.Invoke(this, EventArgs.Empty);
        Show("設定已儲存。連線、分類與 HTTP／影片限速套用於新下載。", InfoBarSeverity.Success);
    }
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, ApplySettings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    private void RestoreHistory()
    {
        try
        {
            // Track inserts at the front. Reverse the database's newest-first ordering once.
            foreach (var stored in _store.Load().Reverse())
            {
                if (!TransferRouting.TryParse(stored.Url, out var uri)) continue;
                var job = App.Queue.Restore(stored.Id, new DownloadRequest
                {
                    Uri = uri, Kind = stored.Kind, PageUrl = stored.PageUrl, MediaOutput = stored.MediaOutput,
                    MediaQuality = stored.MediaQuality, Directory = stored.Directory, FileName = string.IsNullOrEmpty(stored.FileName) ? null : stored.FileName,
                    SortIntoCategories = stored.SortIntoCategories, CategoryRules = stored.CategoryRules,
                    Connections = stored.Connections, BytesPerSecond = stored.BytesPerSecond,
                    ReadTimeoutSeconds = stored.ReadTimeoutSeconds, ExpectedSha256 = stored.ExpectedSha256,
                    Referrer = stored.Referrer,
                }, stored.State == "Completed" ? DownloadState.Completed : DownloadState.Paused, stored.Path);
                job.Average.Restore(stored.TransferredBytes, stored.ActiveSeconds);
                if (job.State == DownloadState.Completed && stored.TotalBytes is { } total)
                    job.Progress = new DownloadProgress(total, total, 0, []);
                Track(job, false);
            }
            if (_settings.ResumeOnStartup) App.Queue.ResumeAll();
        }
        catch (Exception ex) { Show("無法完整讀取下載紀錄：" + ex.Message, InfoBarSeverity.Error); }
    }

    public void AddFromBrowser(CaptureRequest capture) => _dispatcher.TryEnqueue(() =>
    {
        if (FindDuplicate(capture.Request) is { } duplicate)
        {
            Show($"清單中已有 {duplicate.FileName}，沒有重複新增。", InfoBarSeverity.Informational);
            return;
        }
        if (!_settings.PromptOnCapture) { Track(App.Queue.Add(capture.Request)); return; }
        OpenPrompt(capture, true);
    });

    private void OpenPrompt(CaptureRequest capture, bool browser = false)
    {
        var window = new NewDownloadWindow(capture, Accept, browser ? StopAsking : null);
        window.Activate();
        if (_settings.PromptAlwaysOnTop) window.ForceForeground();
    }
    private DownloadJob? FindDuplicate(DownloadRequest request) => !_settings.PreventDuplicateDownloads ? null :
        App.Queue.Jobs.FirstOrDefault(job => job.State is not (DownloadState.Completed or DownloadState.Removed) &&
            job.Request.Uri.AbsoluteUri == request.Uri.AbsoluteUri && job.Request.Kind == request.Kind &&
            job.Request.MediaOutput == request.MediaOutput && job.Request.MediaQuality == request.MediaQuality);

    private DownloadJob Accept(DownloadRequest request, bool start)
    {
        if (FindDuplicate(request) is { } duplicate)
        {
            Show("相同網址已在清單中，使用原有項目。", InfoBarSeverity.Informational);
            if (start) App.Queue.Resume(duplicate.Id);
            return duplicate;
        }
        try
        {
            var updated = _settings with { DownloadFolder = request.Directory, SortIntoCategories = request.SortIntoCategories };
            updated.Save(); _settings = updated;
        }
        catch (Exception ex) { Show("下載已加入，但未儲存預設資料夾：" + ex.Message, InfoBarSeverity.Warning); }
        var job = start ? App.Queue.Add(request) : App.Queue.Restore(Guid.NewGuid(), request, DownloadState.Paused, null);
        Track(job); return job;
    }
    private void StopAsking(bool stop)
    {
        if (!stop) return;
        try { ApplySettings(_settings with { PromptOnCapture = false }); }
        catch (Exception ex) { Show("設定未儲存：" + ex.Message, InfoBarSeverity.Error); }
    }
    private void OnJobChanged(DownloadJob job) => _changes[job.Id] = job;
    private void FlushChanges()
    {
        if (_changes.IsEmpty) return;
        foreach (var id in _changes.Keys)
        {
            if (!_changes.TryRemove(id, out var job) || !_byId.TryGetValue(id, out var row)) continue;
            if (job.State == DownloadState.Removed)
            {
                _rows.Remove(row); _byId.Remove(id); _persisted.Remove(id);
                try { _store.Delete(id); } catch (Exception ex) { Show("紀錄未刪除：" + ex.Message, InfoBarSeverity.Error); }
                continue;
            }
            row.Refresh();
            var settled = job.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Paused;
            if (!_persisted.TryGetValue(id, out var prior) || prior.Request != job.Request || (settled && prior.State != job.State)) Remember(job);
        }
        RefreshDownloadView();
    }
    private void Track(DownloadJob job, bool remember = true)
    {
        var row = new DownloadRowViewModel(job); row.Refresh();
        _rows.Insert(0, row); _byId[job.Id] = row;
        if (remember) Remember(job); else _persisted[job.Id] = (job.Request, job.State);
        if (_ready) RefreshDownloadView();
    }
    private void Remember(DownloadJob job)
    {
        try
        {
            var r = job.Request;
            _store.Save(new StoredDownload(job.Id, r.Uri.AbsoluteUri, r.Directory, r.FileName ?? "", r.Referrer,
                job.State.ToString(), job.Path, DateTimeOffset.UtcNow, r.Kind, r.PageUrl, r.MediaOutput, r.MediaQuality,
                r.Connections, r.BytesPerSecond, r.ReadTimeoutSeconds, r.SortIntoCategories, r.CategoryRules, r.ExpectedSha256,
                job.Average.TransferredBytes, job.Average.ActiveSeconds, job.Progress?.TotalBytes));
            _persisted[job.Id] = (r, job.State);
        }
        catch (Exception ex) { Show("下載紀錄未儲存：" + ex.Message, InfoBarSeverity.Error); }
    }
    public void SaveBeforeExit()
    {
        FlushChanges();
        foreach (var job in App.Queue.Jobs) Remember(job);
    }

    private static bool IsActive(DownloadState state) => state is DownloadState.Queued or DownloadState.Running or DownloadState.Retrying;
    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private void RefreshDownloadView()
    {
        var filter = Tag(StatusFilter);
        var query = SearchBox.Text.Trim();
        IEnumerable<DownloadRowViewModel> wanted = _rows.Where(row => (filter switch
        {
            "active" => IsActive(row.Job.State), "completed" => row.Job.State == DownloadState.Completed,
            "attention" => row.Job.State is DownloadState.Paused or DownloadState.Failed,
            _ => row.Job.State != DownloadState.Removed,
        }) && (query.Length == 0 || row.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            row.Job.Request.Uri.Host.Contains(query, StringComparison.OrdinalIgnoreCase)));
        wanted = Tag(SortOrder) switch
        {
            "name" => wanted.OrderBy(row => row.FileName, StringComparer.CurrentCultureIgnoreCase),
            "source" => wanted.OrderBy(row => row.Job.Request.Uri.Host, StringComparer.OrdinalIgnoreCase),
            "status" => wanted.OrderBy(row => row.Job.State switch
            {
                DownloadState.Running => 0, DownloadState.Queued => 1, DownloadState.Retrying => 2,
                DownloadState.Paused => 3, DownloadState.Failed => 4, DownloadState.Completed => 5,
                _ => 6,
            }),
            "size" => wanted.OrderByDescending(row => row.Job.Progress?.TotalBytes ?? -1),
            _ => wanted,
        };
        var target = wanted.ToArray();
        // Reconcile only changed positions rather than Clear/Add the whole virtualized list.
        var keep = target.Select(row => row.Id).ToHashSet();
        for (var i = _visibleRows.Count - 1; i >= 0; i--) if (!keep.Contains(_visibleRows[i].Id)) _visibleRows.RemoveAt(i);
        for (var i = 0; i < target.Length; i++)
        {
            if (i < _visibleRows.Count && ReferenceEquals(_visibleRows[i], target[i])) continue;
            var existing = _visibleRows.IndexOf(target[i]);
            if (existing >= 0) _visibleRows.Move(existing, i); else _visibleRows.Insert(i, target[i]);
        }
        var active = _rows.Count(row => IsActive(row.Job.State));
        var speed = _rows.Where(row => row.Job.State == DownloadState.Running).Sum(row => row.Job.Progress?.BytesPerSecond ?? 0);
        Summary.Text = $"{_visibleRows.Count} / {_rows.Count} 個下載 · {active} 個進行中 · {DownloadRowViewModel.Bytes((long)speed)}/s · 已選取 {Downloads.SelectedItems.Count} 個";
        PauseAllButton.IsEnabled = active > 0;
        ResumeAllButton.IsEnabled = _rows.Any(row => row.Job.State is DownloadState.Paused or DownloadState.Failed);
        ClearCompletedButton.IsEnabled = _rows.Any(row => row.Job.State == DownloadState.Completed);
        EmptyState.Visibility = target.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Downloads.Visibility = target.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _rows.Count == 0 ? "還沒有下載" : "沒有符合條件的下載";
        EmptyDescription.Text = _rows.Count == 0 ? "貼上下載連結、影片頁面或磁力連結。多個網址可用「批次新增」，不必逐一開視窗。" : "試試其他關鍵字，或切換回「全部下載」。";
    }
    private void StatusFilterChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RefreshDownloadView(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_ready) RefreshDownloadView(); }
    private void DownloadSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var prefix = Summary.Text.Split(" · 已選取", StringSplitOptions.None)[0];
        Summary.Text = $"{prefix} · 已選取 {Downloads.SelectedItems.Count} 個";
    }
    private Guid[] SelectedIds() => Downloads.SelectedItems.OfType<DownloadRowViewModel>().Select(row => row.Id).ToArray();
    private void PauseSelectedClick(object sender, RoutedEventArgs e) { foreach (var id in SelectedIds()) App.Queue.Pause(id); }
    private void ResumeSelectedClick(object sender, RoutedEventArgs e) { foreach (var id in SelectedIds()) App.Queue.Resume(id); }
    private void RemoveSelectedClick(object sender, RoutedEventArgs e) { foreach (var id in SelectedIds()) App.Queue.Remove(id); }
    private void CopySelectedClick(object sender, RoutedEventArgs e)
    {
        var urls = Downloads.SelectedItems.OfType<DownloadRowViewModel>().Select(row => row.Job.Request.Uri.AbsoluteUri).ToArray();
        if (urls.Length == 0) { Show("先選取要複製的下載。可按住 Ctrl 或 Shift 多選。", InfoBarSeverity.Informational); return; }
        try { CopyText(string.Join(Environment.NewLine, urls)); Show("網址已複製。請勿公開含登入權杖的連結。", InfoBarSeverity.Success); }
        catch (Exception ex) { Show("無法寫入剪貼簿：" + ex.Message, InfoBarSeverity.Error); }
    }
    private void ClearCompletedClick(object sender, RoutedEventArgs e)
    { foreach (var id in _rows.Where(row => row.Job.State == DownloadState.Completed).Select(row => row.Id).ToArray()) App.Queue.Remove(id); }
    private DownloadRequest NewRequest(Uri uri, TransferKind? kind = null) => new()
    {
        Uri = uri, Kind = kind ?? TransferRouting.For(uri), PageUrl = (kind ?? TransferRouting.For(uri)) == TransferKind.Media ? uri.AbsoluteUri : null,
        Directory = IngestListener.DownloadFolder(_settings), SortIntoCategories = _settings.SortIntoCategories,
        CategoryRules = _settings.CategoryRules, Connections = _settings.Connections,
        BytesPerSecond = _settings.BytesPerSecond, ReadTimeoutSeconds = _settings.ReadTimeoutSeconds,
    };
    private async Task<string> ClipboardTextAsync()
    {
        var content = Clipboard.GetContent();
        return content.Contains(StandardDataFormats.Text) ? (await content.GetTextAsync()).Trim() : "";
    }
    private async void PasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = await ClipboardTextAsync();
            if (text.Contains('\n') || text.Contains('\r')) { await BatchAsync(text); return; }
            if (!TransferRouting.TryParse(text, out var uri)) { Show("剪貼簿裡沒有有效網址。", InfoBarSeverity.Informational); return; }
            OpenPrompt(new(NewRequest(uri), 0));
        }
        catch (Exception ex) { Show("無法讀取剪貼簿：" + ex.Message, InfoBarSeverity.Error); }
    }
    private async void PasteMediaClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TransferRouting.TryParse(await ClipboardTextAsync(), out var uri) || uri.Scheme == "magnet")
            { Show("請先複製 HTTP 或 HTTPS 影片頁面網址。", InfoBarSeverity.Informational); return; }
            OpenPrompt(new(NewRequest(uri, TransferKind.Media), 0));
        }
        catch (Exception ex) { Show("無法新增影片：" + ex.Message, InfoBarSeverity.Error); }
    }
    private async void BatchClick(object sender, RoutedEventArgs e) => await BatchAsync("");
    private async Task BatchAsync(string text)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 180,
                MaxLength = 1_048_576, PlaceholderText = "每行一個 HTTP、HTTPS 或 magnet 網址", Text = text };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "批次網址");
            var later = new CheckBox { Content = "只加入清單，稍後再下載", IsChecked = true };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
            var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
            panel.Children.Add(new TextBlock { Text = "一次最多 500 個網址。會使用目前的下載資料夾與分類設定，重複網址只保留一份。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input); panel.Children.Add(later); panel.Children.Add(error);
            LinkList? parsed = null;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "批次新增下載", Content = panel,
                PrimaryButtonText = "加入清單", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { parsed = LinkList.Parse(input.Text); }
                catch (ArgumentException ex) { error.Text = ex.Message; args.Cancel = true; }
            };
            if (await _dialogs.ShowAsync(dialog) != ContentDialogResult.Primary || parsed is null) return;
            var added = 0;
            var skipped = parsed.Duplicates;
            // Batch additions avoid hundreds of list reconciliation passes.
            _ready = false;
            try
            {
                foreach (var uri in parsed.Links)
                {
                    var request = NewRequest(uri);
                    if (FindDuplicate(request) is not null) { skipped++; continue; }
                    Track(later.IsChecked == true ? App.Queue.Restore(Guid.NewGuid(), request, DownloadState.Paused, null) : App.Queue.Add(request));
                    added++;
                }
            }
            finally { _ready = true; RefreshDownloadView(); }
            Show($"已加入 {added} 個下載，略過 {skipped} 個重複網址。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Show("批次新增未完成：" + ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }
    private async void ImportLinksClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".txt");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            if (new FileInfo(file.Path).Length > 1_048_576) throw new ArgumentException("網址清單最多 1 MiB。");
            await BatchAsync(await File.ReadAllTextAsync(file.Path));
        }
        catch (Exception ex) { Show("匯入失敗：" + ex.Message, InfoBarSeverity.Error); }
    }
    private async void ExportLinksClick(object sender, RoutedEventArgs e)
    {
        if (_visibleRows.Count == 0) { Show("目前檢視沒有可匯出的下載。", InfoBarSeverity.Informational); return; }
        try
        {
            var urls = _visibleRows.Select(row => row.Job.Request.Uri.AbsoluteUri).ToArray();
            var picker = new FileSavePicker { SuggestedFileName = "Downlism-links" };
            picker.FileTypeChoices.Add("網址清單（可能包含私人權杖，請勿公開）", new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync(); if (file is null) return;
            await File.WriteAllLinesAsync(file.Path, urls);
            Show($"已匯出 {urls.Length} 個網址。連結可能含登入權杖，請勿公開。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Show("匯出失敗：" + ex.Message, InfoBarSeverity.Error); }
    }
    private async void OnClipboardChanged(object? sender, object e)
    {
        try
        {
            var text = await ClipboardTextAsync();
            if (text == _lastClipboardUrl || !TransferRouting.TryParse(text, out var uri)) return;
            _lastClipboardUrl = text;
            if (_clipboardPromptOpen) return;
            _clipboardPromptOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "剪貼簿有下載連結",
                    Content = "要確認這個連結並新增下載嗎？",
                    PrimaryButtonText = "新增下載", CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
                    OpenPrompt(new(NewRequest(uri), 0));
            }
            finally { _clipboardPromptOpen = false; }
        }
        catch (Exception) { /* Clipboard may be held by another application. */ }
    }
    private async void HashClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || !_byId.TryGetValue(id, out var row) || row.Job.Path is not { } path) return;
        try
        {
            if (!File.Exists(path)) { Show("檔案已移動、刪除，或這個下載是資料夾。", InfoBarSeverity.Warning); return; }
            var control = sender as Control;
            if (control is not null) control.IsEnabled = false;
            string hash;
            try { hash = await FileHash.ComputeAsync(path, FileHash.Algorithm.Sha256); }
            finally { if (control is not null) control.IsEnabled = true; }
            CopyText(hash); Show("SHA-256 已複製：" + hash, InfoBarSeverity.Success);
        }
        catch (Exception ex) { Show("無法計算雜湊：" + ex.Message, InfoBarSeverity.Error); }
    }
    private static void CopyText(string text) { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
    private void PauseAllClick(object sender, RoutedEventArgs e) => App.Queue.PauseAll();
    private void ResumeAllClick(object sender, RoutedEventArgs e) => App.Queue.ResumeAll();
    private void PauseClick(object sender, RoutedEventArgs e) => WithId(sender, App.Queue.Pause);
    private void ResumeClick(object sender, RoutedEventArgs e) => WithId(sender, App.Queue.Resume);
    private void RemoveClick(object sender, RoutedEventArgs e) => WithId(sender, App.Queue.Remove);
    private void OpenFolderClick(object sender, RoutedEventArgs e) => WithId(sender, id =>
    {
        if (!_byId.TryGetValue(id, out var row) || row.Job.Path is not { } path) return;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { Show("檔案已移動或刪除。", InfoBarSeverity.Warning); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Show("無法開啟檔案位置：" + ex.Message, InfoBarSeverity.Error); }
    });
    private static void WithId(object sender, Action<Guid> action) { if (sender is FrameworkElement { Tag: Guid id }) action(id); }
    private void ExtensionGuideClick(object sender, RoutedEventArgs e) => ShowExtensionGuide();
    public void ShowExtensionGuide() => new ExtensionGuideWindow().Activate();
    public void ShowExtensionGuideOnFirstFrame()
    {
        Root.Loaded += OnFirstLoad;
        void OnFirstLoad(object sender, RoutedEventArgs e) { Root.Loaded -= OnFirstLoad; _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, ShowExtensionGuide); }
    }
    private void Show(string message, InfoBarSeverity severity)
        => _dialogs.ShowMessage(message, severity);
}
