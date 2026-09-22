using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Downlism.App.Services;

/// <summary>Serializes native dialogs within one window, including messages arriving before its first frame.</summary>
internal sealed class WindowDialogs
{
    private readonly FrameworkElement _root;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ContentDialog? _active;

    public WindowDialogs(Window window, FrameworkElement root)
    {
        _root = root;
        if (root.IsLoaded) _loaded.TrySetResult();
        else root.Loaded += OnLoaded;
        window.Closed += (_, _) =>
        {
            root.Loaded -= OnLoaded;
            _closed.Cancel();
            _active?.Hide();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= OnLoaded;
        _loaded.TrySetResult();
    }

    public async void ShowMessage(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        await ShowAsync(new ContentDialog
        {
            Title = severity switch
            {
                InfoBarSeverity.Error => "無法完成操作",
                InfoBarSeverity.Warning => "請注意",
                InfoBarSeverity.Success => "已完成",
                _ => "提示",
            },
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = "確定",
            DefaultButton = ContentDialogButton.Close,
        });
    }

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var entered = false;
        try
        {
            await _loaded.Task.WaitAsync(_closed.Token);
            await _gate.WaitAsync(_closed.Token);
            entered = true;
            _closed.Token.ThrowIfCancellationRequested();
            dialog.XamlRoot = _root.XamlRoot;
            dialog.RequestedTheme = _root.ActualTheme;
            _active = dialog;
            return await dialog.ShowAsync();
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { return ContentDialogResult.None; }
        catch (System.Runtime.InteropServices.COMException) when (_closed.IsCancellationRequested) { return ContentDialogResult.None; }
        finally
        {
            if (entered) { _active = null; _gate.Release(); }
        }
    }
}
