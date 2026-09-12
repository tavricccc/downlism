using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Downlism.App.Services;
using Downlism.Core.Downloads;

namespace Downlism.App.ViewModels;

/// <summary>One row of the list.</summary>
public sealed partial class DownloadRowViewModel(DownloadJob job) : ObservableObject
{
    public Guid Id => job.Id;

    public DownloadJob Job => job;

    public string FileName => job.FileName;

    public string Host => job.Request.Uri.Host;

    [ObservableProperty]
    public partial IReadOnlyList<Segment>? Segments { get; set; }

    [ObservableProperty]
    public partial double Fraction { get; set; }

    [ObservableProperty]
    public partial string Size { get; set; } = "—";

    [ObservableProperty]
    public partial string Speed { get; set; } = "";

    [ObservableProperty]
    public partial string Remaining { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "排隊中";

    [ObservableProperty]
    public partial string Tone { get; set; } = "running";

    [ObservableProperty]
    public partial bool CanPause { get; set; }

    [ObservableProperty]
    public partial bool CanResume { get; set; }

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    public void Refresh()
    {
        var progress = job.Progress;

        Segments = progress?.Segments is { Count: > 0 } segments ? segments : null;
        Fraction = progress?.Fraction ?? 0;

        Size = progress is null
            ? "—"
            : progress.TotalBytes is > 0
                ? $"{Bytes(progress.CompletedBytes)} / {Bytes(progress.TotalBytes.Value)}"
                : Bytes(progress.CompletedBytes);

        var running = job.State == DownloadState.Running;
        Speed = running && progress is { BytesPerSecond: > 1 } ? $"{Bytes((long)progress.BytesPerSecond)}/s" : "";
        Remaining = running && progress?.Remaining is { } left ? Duration(left) : "";

        Tone = job.State switch
        {
            DownloadState.Completed => "completed",
            DownloadState.Failed => "failed",
            DownloadState.Paused => "paused",
            _ => "running",
        };

        Status = job.State switch
        {
            DownloadState.Queued => "排隊中",
            DownloadState.Running => job.Request.Uri.Host,
            DownloadState.Paused => "已暫停",
            DownloadState.Completed => "已完成",
            DownloadState.Failed => job.Error ?? "下載失敗",
            _ => "",
        };

        CanPause = job.State is DownloadState.Running or DownloadState.Queued;
        CanResume = job.State is DownloadState.Paused or DownloadState.Failed;
        IsFinished = job.State == DownloadState.Completed;
    }

    /// <summary>Lets x:Bind drive visibility without a converter resource.</summary>
    public static Visibility ShowIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Binary units, because that is what the file system reports and what the user will see
    /// in Explorer a moment later.
    /// </summary>
    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // One decimal below 100 keeps the column width stable as the number grows.
        return unit == 0 ? $"{value} B" : size < 100 ? $"{size:0.0} {units[unit]}" : $"{size:0} {units[unit]}";
    }

    public static string Duration(TimeSpan value) => value switch
    {
        { TotalSeconds: < 60 } => $"剩 {value.TotalSeconds:0} 秒",
        { TotalMinutes: < 60 } => $"剩 {value.TotalMinutes:0} 分",
        { TotalHours: < 24 } => $"剩 {value.Hours} 小時 {value.Minutes} 分",
        _ => "剩超過一天",
    };
}
