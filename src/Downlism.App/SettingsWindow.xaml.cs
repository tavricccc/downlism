using Downlism.App.Services;
using Downlism.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Downlism.App;

public sealed partial class SettingsWindow : Window
{
    private readonly Action<AppSettings> _apply;
    private readonly LoginStartupService _startup = new();
    private AppSettings _draft;

    public SettingsWindow(AppSettings settings, Action<AppSettings> apply)
    {
        InitializeComponent();
        _draft = settings;
        _apply = apply;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 820));
        AppWindow.SetIcon("Assets/Downlism.ico");
        Startup.IsChecked = _startup.IsEnabled();
        Fill(settings);
        App.ApplyTheme(Root, settings.Theme);
    }

    private void Fill(AppSettings settings)
    {
        _draft = settings;
        Folder.Text = settings.DownloadFolder ?? "";
        Connections.Value = settings.Connections;
        Concurrent.Value = settings.ConcurrentDownloads;
        Rate.Value = (double)settings.BytesPerSecond / 1024;
        TimeoutSeconds.Value = settings.ReadTimeoutSeconds;
        Attempts.Value = settings.RetryAttempts;
        RetryDelay.Value = settings.RetryDelaySeconds;
        Categories.IsChecked = settings.SortIntoCategories;
        Duplicates.IsChecked = settings.PreventDuplicateDownloads;
        ResumeStartup.IsChecked = settings.ResumeOnStartup;
        Tray.IsChecked = settings.CloseToTray;
        ClipboardWatch.IsChecked = settings.WatchClipboard;
        CapturePrompt.IsChecked = settings.PromptOnCapture;
        ProgressWindow.IsChecked = settings.KeepProgressWindow;
        AlwaysOnTop.IsChecked = settings.PromptAlwaysOnTop;
        CompletedNotice.IsChecked = settings.NotifyOnComplete;
        FailedNotice.IsChecked = settings.NotifyOnFailure;
        Rules.Text = settings.CategoryRules;
        ThemeChoice.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
    }

    private static int Integer(NumberBox box)
    {
        if (!double.IsFinite(box.Value) || box.Value != Math.Truncate(box.Value)) throw new ArgumentException("次數與秒數請輸入整數。");
        return checked((int)box.Value);
    }

    private AppSettings ReadDraft()
    {
        if (!double.IsFinite(Rate.Value)) throw new ArgumentException("請輸入有效的速度上限。");
        var settings = _draft with
        {
            DownloadFolder = string.IsNullOrWhiteSpace(Folder.Text) ? null : Folder.Text.Trim(),
            Connections = Integer(Connections), ConcurrentDownloads = Integer(Concurrent),
            BytesPerSecond = checked((long)Math.Round(Rate.Value * 1024)),
            ReadTimeoutSeconds = Integer(TimeoutSeconds), RetryAttempts = Integer(Attempts), RetryDelaySeconds = Integer(RetryDelay),
            SortIntoCategories = Categories.IsChecked == true, PreventDuplicateDownloads = Duplicates.IsChecked == true,
            ResumeOnStartup = ResumeStartup.IsChecked == true, CloseToTray = Tray.IsChecked == true,
            WatchClipboard = ClipboardWatch.IsChecked == true, PromptOnCapture = CapturePrompt.IsChecked == true,
            KeepProgressWindow = ProgressWindow.IsChecked == true, PromptAlwaysOnTop = AlwaysOnTop.IsChecked == true,
            NotifyOnComplete = CompletedNotice.IsChecked == true, NotifyOnFailure = FailedNotice.IsChecked == true,
            CategoryRules = Rules.Text.Trim(), Theme = (ThemeChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "System",
        };
        settings.Validate();
        return settings;
    }

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) Folder.Text = folder.Path;
        }
        catch (Exception ex) { Error(ex); }
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadDraft();
            if (_startup.IsEnabled() != (Startup.IsChecked == true)) _startup.SetEnabled(Startup.IsChecked == true);
            _apply(settings);
            Close();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void DefaultsClick(object sender, RoutedEventArgs e)
    {
        Fill(new AppSettings());
        Notice.Message = "已載入預設值，按「儲存」才會套用。開機自啟維持原選擇。";
        Notice.Severity = InfoBarSeverity.Informational; Notice.IsOpen = true;
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadDraft();
            var picker = new FileSavePicker { SuggestedFileName = "Downlism-settings" };
            picker.FileTypeChoices.Add("JSON 設定", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, settings.ToJson());
            Notice.Message = "設定已匯出。"; Notice.Severity = InfoBarSeverity.Success; Notice.IsOpen = true;
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            if (new FileInfo(file.Path).Length > 65_536) throw new ArgumentException("設定檔過大，請選擇 Downlism 匯出的 JSON。");
            var settings = AppSettings.FromJson(await File.ReadAllTextAsync(file.Path));
            settings.Validate(); Fill(settings);
            Notice.Message = "已讀取設定，確認後按「儲存」套用。"; Notice.Severity = InfoBarSeverity.Informational; Notice.IsOpen = true;
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Error(Exception ex)
    {
        Notice.Message = "未儲存：" + ex.Message; Notice.Severity = InfoBarSeverity.Error; Notice.IsOpen = true;
    }
}
