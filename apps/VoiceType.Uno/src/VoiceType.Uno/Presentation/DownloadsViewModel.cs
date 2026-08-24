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

    public void Detach() => _queue.Changed -= OnQueueChanged;

    private void OnQueueChanged() => _dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        var snapshot = _queue.Items;

        // Remove view-models whose items disappeared (queue is append-only for
        // now, so this is mostly a no-op, kept for correctness).
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
                Items.Add(new DownloadItemViewModel(item));
            else
                vm.Update();
        }

        var aggregate = _queue.GetAggregateProgress();
        AggregatePercent = aggregate.Percent;
        IsEmpty = snapshot.Count == 0;
        AggregateText = snapshot.Count == 0
            ? "No downloads"
            : $"Queue: {aggregate.Percent:F0}% — {aggregate.CompletedItems}/{aggregate.TotalItems} done";
    }
}

/// <summary>View-model wrapper around a queue item with change notification.</summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadQueueItem _item;

    public DownloadItemViewModel(DownloadQueueItem item)
    {
        _item = item;
        Update();
    }

    public Guid Id => _item.Id;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string _stateText = "";

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
}
