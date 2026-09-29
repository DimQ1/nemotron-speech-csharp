using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using SpeechLib.ModelDownload;

namespace VoiceType.WinUI.ViewModels;

/// <summary>One row in the download manager: a snapshot of a <see cref="DownloadJob"/>, refreshed on the UI thread.</summary>
public sealed partial class DownloadJobViewModel : ObservableObject
{
    public DownloadJobViewModel(DownloadJob job)
    {
        Job = job;
        Refresh();
    }

    public DownloadJob Job { get; }
    public string Title => Job.Title;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canUse;
    [ObservableProperty] private bool _isIndeterminate;

    public Visibility CancelVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => CanRetry ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UseVisibility => CanUse ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(CancelVisibility));
    partial void OnCanRetryChanged(bool value) => OnPropertyChanged(nameof(RetryVisibility));
    partial void OnCanUseChanged(bool value) => OnPropertyChanged(nameof(UseVisibility));

    /// <summary>Copies the job's current values; call on the UI thread.</summary>
    public void Refresh()
    {
        var job = Job;
        Percent = job.Percent;
        IsActive = job.IsActive;
        CanRetry = job.State is DownloadJobState.Failed or DownloadJobState.Cancelled;
        CanUse = job.State == DownloadJobState.Completed && job.Request.Kind == ModelDownloadKind.Recognition;
        IsIndeterminate = job.State == DownloadJobState.Downloading && job.TotalBytes == 0;

        StateText = job.State switch
        {
            DownloadJobState.Queued => "Queued",
            DownloadJobState.Downloading => $"{job.Percent:F0}%",
            DownloadJobState.Completed => "Completed",
            DownloadJobState.Failed => "Failed",
            DownloadJobState.Cancelled => "Cancelled",
            _ => "",
        };

        Detail = job.State switch
        {
            DownloadJobState.Queued => "Waiting for a free download slot…",
            DownloadJobState.Downloading => BuildProgressDetail(job),
            DownloadJobState.Completed => $"{Format(job.TotalBytes)} · {job.FilesTotal} file(s) · {job.ResultPath}",
            DownloadJobState.Failed => job.Error ?? "Unknown error",
            DownloadJobState.Cancelled => $"{Format(job.DownloadedBytes)} of {Format(job.TotalBytes)} kept — Retry resumes",
            _ => "",
        };
    }

    private static string BuildProgressDetail(DownloadJob job)
    {
        var parts = new List<string>();
        if (job.TotalBytes > 0)
            parts.Add($"{Format(job.DownloadedBytes)} / {Format(job.TotalBytes)}");
        if (job.BytesPerSecond > 0)
        {
            parts.Add($"{Format((long)job.BytesPerSecond)}/s");
            var remaining = job.TotalBytes - job.DownloadedBytes;
            if (remaining > 0)
                parts.Add(FormatEta(TimeSpan.FromSeconds(remaining / job.BytesPerSecond)));
        }
        if (job.FilesTotal > 0)
            parts.Add($"file {Math.Min(job.FilesDone + 1, job.FilesTotal)}/{job.FilesTotal}");
        if (!string.IsNullOrEmpty(job.CurrentFile))
            parts.Add(job.CurrentFile);
        return string.Join(" · ", parts);
    }

    internal static string Format(long bytes) => ModelMetricsFormatter.FormatSize(bytes);

    internal static string FormatEta(TimeSpan eta) => eta.TotalHours >= 1
        ? $"{(int)eta.TotalHours} h {eta.Minutes} min left"
        : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes} min {eta.Seconds} s left" : $"{eta.Seconds} s left";
}
