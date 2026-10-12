using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// ViewModel for the Downloads tab of the Models page: lists every
/// queued/active/finished download with per-item progress, and the aggregate
/// progress of the queue. It reads the shared <see cref="ModelDownloadManager"/> —
/// the same pipeline the main window, the Speech tab and the WinUI app use.
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

    /// <summary>Summary headline, e.g. "2 active · 1 failed".</summary>
    [ObservableProperty]
    private string _aggregateText = "No downloads";

    /// <summary>Bytes over the whole queue, e.g. "1.0 / 2.4 GB".</summary>
    [ObservableProperty]
    private string _aggregateTransferText = "";

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private bool _hasFinished;

    /// <summary>How many downloads are queued or running right now.</summary>
    [ObservableProperty]
    private int _activeCount;

    /// <summary>"Downloads · 2" while jobs are active, "Downloads" otherwise.</summary>
    [ObservableProperty]
    private string _tabLabel = "Downloads";

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
        HasItems = !IsEmpty;
        HasFinished = snapshot.Any(job => !job.IsActive);
        ActiveCount = totals.Active;
        TabLabel = ModelsPageFormatter.DownloadsTabLabel(totals.Active);
        AggregateText = ModelsPageFormatter.DescribeQueueHeadline(totals.Active, totals.Failed, totals.Completed, snapshot.Count);
        AggregateTransferText = ModelsPageFormatter.DescribeTransfer(totals.DownloadedBytes, totals.TotalBytes);
    }
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
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isFinished;

    /// <summary>Left of the line under the bar: "42% · encoder.onnx.data", "Queued", "Completed · 697 MB", "Failed — …".</summary>
    [ObservableProperty] private string _detailText = "";

    /// <summary>Failure reason, shown in the error colour in place of <see cref="DetailText"/>.</summary>
    [ObservableProperty] private string _errorText = "";

    /// <summary>Right of the line under the bar: "1.0 / 2.4 GB" while active.</summary>
    [ObservableProperty] private string _transferText = "";

    /// <summary>Left of the actions: "Downloading · 3.1 MB/s · 7 min".</summary>
    [ObservableProperty] private string _stateText = "";

    [RelayCommand]
    private void Cancel() => _downloads.Cancel(_job);

    [RelayCommand]
    private void Remove() => _downloads.Remove(_job);

    [RelayCommand]
    private void Retry() => _downloads.Retry(_job);

    public void Update()
    {
        DisplayName = _job.Title;

        Percent = _job.Percent;
        IsRunning = _job.IsActive;
        IsProgressVisible = _job.State == DownloadJobState.Downloading;
        IsIndeterminate = _job.TotalBytes == 0 && IsProgressVisible;
        IsFailed = _job.State is DownloadJobState.Failed or DownloadJobState.Cancelled;
        IsError = _job.State == DownloadJobState.Failed;
        IsFinished = !_job.IsActive;

        DetailText = _job.State switch
        {
            DownloadJobState.Queued => "Queued",
            DownloadJobState.Downloading => _job.TotalBytes > 0
                ? string.IsNullOrEmpty(_job.CurrentFile) ? $"{_job.Percent:F0}%" : $"{_job.Percent:F0}% · {_job.CurrentFile}"
                : string.IsNullOrEmpty(_job.CurrentFile) ? "Starting…" : _job.CurrentFile,
            DownloadJobState.Completed => _job.TotalBytes > 0
                ? $"Completed · {DownloadProgressFormatter.FormatBytes(_job.TotalBytes)}"
                : "Completed",
            DownloadJobState.Cancelled => "Cancelled — partial files kept",
            _ => ""
        };

        ErrorText = IsError ? $"Failed — {_job.Error ?? "unknown error"}; partial files kept" : "";

        TransferText = _job.IsActive
            ? ModelsPageFormatter.DescribeTransfer(_job.DownloadedBytes, _job.TotalBytes)
            : "";

        var rate = ModelsPageFormatter.DescribeRate(_job.BytesPerSecond, _job.DownloadedBytes, _job.TotalBytes);
        StateText = _job.State == DownloadJobState.Downloading
            ? rate.Length > 0 ? $"Downloading · {rate}" : "Downloading"
            : "";
    }
}
