using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// ViewModel for the Downloads window: lists every queued/active/finished
/// download with per-item progress, and the aggregate progress of the queue.
/// It reads the shared <see cref="ModelDownloadManager"/> — the same pipeline the
/// main window, the model manager and the WinUI app use.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly ModelDownloadManager _downloads;
    private readonly DispatcherQueue _dispatcher;

    public DownloadsViewModel(ModelDownloadManager downloads, DispatcherQueue dispatcher)
    {
        _downloads = downloads;
        _dispatcher = dispatcher;
        _downloads.JobAdded += OnJobChanged;
        _downloads.JobUpdated += OnJobChanged;
        _downloads.JobFinished += OnJobChanged;
        _downloads.JobRemoved += OnJobChanged;
        Refresh();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private double _aggregatePercent;

    [ObservableProperty]
    private string _aggregateText = "No downloads";

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>How many downloads are queued or running right now.</summary>
    [ObservableProperty]
    private string _activeCountText = "";

    /// <summary>Unsubscribes from the download manager when the host view closes.</summary>
    public void Detach()
    {
        _downloads.JobAdded -= OnJobChanged;
        _downloads.JobUpdated -= OnJobChanged;
        _downloads.JobFinished -= OnJobChanged;
        _downloads.JobRemoved -= OnJobChanged;
    }

    /// <summary>Removes every finished (completed/failed/cancelled) job from the list.</summary>
    [RelayCommand]
    private void ClearFinished() => _downloads.ClearFinished();

    private void OnJobChanged(DownloadJob job) => _dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        var snapshot = _downloads.Jobs;

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (snapshot.All(job => job.Id != Items[i].Id))
                Items.RemoveAt(i);
        }

        // Update existing, append new.
        foreach (var job in snapshot)
        {
            var vm = Items.FirstOrDefault(x => x.Id == job.Id);
            if (vm is null)
                Items.Add(new DownloadItemViewModel(job, _downloads));
            else
                vm.Update();
        }

        var totals = _downloads.Totals;
        AggregatePercent = totals.Percent;
        IsEmpty = snapshot.Count == 0;
        ActiveCountText = totals.Active > 0 ? $"{totals.Active} active" : "";
        AggregateText = snapshot.Count == 0
            ? "No downloads"
            : totals.TotalBytes > 0
                ? $"Queue: {totals.Percent:F0}% — {FormatBytes(totals.DownloadedBytes)} / {FormatBytes(totals.TotalBytes)} — {totals.Completed}/{snapshot.Count} done"
                : $"Queue: {totals.Completed}/{snapshot.Count} done";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>View-model wrapper around a download job with change notification.</summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadJob _job;
    private readonly ModelDownloadManager _downloads;

    public DownloadItemViewModel(DownloadJob job, ModelDownloadManager downloads)
    {
        _job = job;
        _downloads = downloads;
        Update();
    }

    public Guid Id => _job.Id;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _sizeText = "";

    [RelayCommand]
    private void Cancel() => _downloads.Cancel(_job);

    [RelayCommand]
    private void Remove() => _downloads.Remove(_job);

    [RelayCommand]
    private void Retry() => _downloads.Retry(_job);

    public void Update()
    {
        DisplayName = _job.Title;

        var state = StateTextOf(_job);
        Status = _job.Error is not null ? $"{state}: {_job.Error}" : state;

        Percent = _job.Percent;
        IsRunning = _job.IsActive;
        IsIndeterminate = _job.TotalBytes == 0 && IsRunning;
        IsFailed = _job.State is DownloadJobState.Failed or DownloadJobState.Cancelled;
        IsFinished = !_job.IsActive;

        var size = _job.TotalBytes > 0
            ? $"{FormatBytesStatic(_job.DownloadedBytes)} / {FormatBytesStatic(_job.TotalBytes)}"
            : _job.DownloadedBytes > 0
                ? FormatBytesStatic(_job.DownloadedBytes)
                : "";
        SizeText = IsRunning && _job.BytesPerSecond > 0
            ? string.IsNullOrEmpty(size)
                ? $"{FormatBytesStatic((long)_job.BytesPerSecond)}/s"
                : $"{size} · {FormatBytesStatic((long)_job.BytesPerSecond)}/s"
            : size;

        StateText = _job.State switch
        {
            DownloadJobState.Queued => "Queued",
            DownloadJobState.Downloading => $"{_job.Percent:F0}%",
            DownloadJobState.Completed => "Done",
            DownloadJobState.Failed => "Failed",
            DownloadJobState.Cancelled => "Cancelled",
            _ => ""
        };
    }

    private static string StateTextOf(DownloadJob job) => job.State switch
    {
        DownloadJobState.Queued => "Queued",
        DownloadJobState.Downloading => job.FilesTotal > 0
            ? $"Downloading {job.CurrentFile} ({job.FilesDone}/{job.FilesTotal})"
            : string.IsNullOrEmpty(job.CurrentFile) ? "Downloading..." : $"Downloading {job.CurrentFile}",
        DownloadJobState.Completed => "Completed",
        DownloadJobState.Failed => "Failed",
        DownloadJobState.Cancelled => "Cancelled",
        _ => ""
    };

    private static string FormatBytesStatic(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };
}
