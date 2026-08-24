using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// ViewModel for the Downloads window: lists every queued/active/finished
/// download with per-item progress, and the aggregate progress of the queue.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly DownloadQueueService _queue;
    private readonly DispatcherQueue _dispatcher;

    public DownloadsViewModel(DownloadQueueService queue, DispatcherQueue dispatcher)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _queue.Changed += OnQueueChanged;
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

    public void Detach() => _queue.Changed -= OnQueueChanged;

    /// <summary>Removes every finished (completed/failed/cancelled) item from the list.</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in _queue.Items
            .Where(i => i.State is DownloadQueueItemState.Completed
                or DownloadQueueItemState.Failed
                or DownloadQueueItemState.Cancelled)
            .ToList())
        {
            _queue.Remove(item.Id);
        }
    }

    private void OnQueueChanged() => _dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        var snapshot = _queue.Items;

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (snapshot.All(s => s.Id != Items[i].Id))
                Items.RemoveAt(i);
        }

        // Update existing, append new.
        foreach (var item in snapshot)
        {
            var vm = Items.FirstOrDefault(x => x.Id == item.Id);
            if (vm is null)
                Items.Add(new DownloadItemViewModel(item, _queue));
            else
                vm.Update();
        }

        var aggregate = _queue.GetAggregateProgress();
        AggregatePercent = aggregate.Percent;
        IsEmpty = snapshot.Count == 0;
        ActiveCountText = aggregate.ActiveItems > 0
            ? $"{aggregate.ActiveItems} active"
            : "";
        AggregateText = snapshot.Count == 0
            ? "No downloads"
            : aggregate.TotalBytes > 0
                ? $"Queue: {aggregate.Percent:F0}% — {FormatBytes(aggregate.DownloadedBytes)} / {FormatBytes(aggregate.TotalBytes)} — {aggregate.CompletedItems}/{aggregate.TotalItems} done"
                : $"Queue: {aggregate.CompletedItems}/{aggregate.TotalItems} done";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>View-model wrapper around a queue item with change notification.</summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadQueueItem _item;
    private readonly DownloadQueueService _queue;

    public DownloadItemViewModel(DownloadQueueItem item, DownloadQueueService queue)
    {
        _item = item;
        _queue = queue;
        Update();
    }

    public Guid Id => _item.Id;

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
    private void Cancel() => _queue.Cancel(Id);

    [RelayCommand]
    private void Remove() => _queue.Remove(Id);

    [RelayCommand]
    private void Retry() => _queue.Retry(Id);

    public void Update()
    {
        DisplayName = _item.DisplayName;
        Status = _item.ErrorMessage is not null
            ? $"{_item.Status}: {_item.ErrorMessage}"
            : _item.Status;
        Percent = _item.Percent;
        IsRunning = _item.State is DownloadQueueItemState.Queued or DownloadQueueItemState.Running;
        IsIndeterminate = _item.TotalBytes == 0 && IsRunning;
        IsFailed = _item.State is DownloadQueueItemState.Failed or DownloadQueueItemState.Cancelled;
        IsFinished = _item.State is DownloadQueueItemState.Completed
            or DownloadQueueItemState.Failed
            or DownloadQueueItemState.Cancelled;
        SizeText = _item.TotalBytes > 0
            ? $"{FormatBytesStatic(_item.DownloadedBytes)} / {FormatBytesStatic(_item.TotalBytes)}"
            : "";
        StateText = _item.State switch
        {
            DownloadQueueItemState.Queued => "Queued",
            DownloadQueueItemState.Running => $"{_item.Percent:F0}%",
            DownloadQueueItemState.Completed => "Done",
            DownloadQueueItemState.Failed => "Failed",
            DownloadQueueItemState.Cancelled => "Cancelled",
            _ => ""
        };
    }

    private static string FormatBytesStatic(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };
}
