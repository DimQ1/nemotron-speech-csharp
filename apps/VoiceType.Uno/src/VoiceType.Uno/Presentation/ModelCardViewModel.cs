using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Services;
namespace VoiceType.Uno.Presentation;

/// <summary>
/// One catalog model shown as a card in the model manager. Exposes the
/// human-readable naming and metrics from <see cref="ModelDescriptor"/> plus the
/// live state (installed / in use / downloading) needed to pick a model.
/// </summary>
public sealed partial class ModelCardViewModel : ObservableObject
{
    private readonly ModelManagerViewModel _owner;
    private readonly DispatcherQueue _dispatcher;
    private DownloadJob? _job;
    private Action<DownloadJob>? _jobHandler;

    public ModelCardViewModel(
        ModelDescriptor descriptor,
        ModelRankingTable ranking,
        ModelManagerViewModel owner,
        DispatcherQueue dispatcher)
    {
        Descriptor = descriptor;
        _owner = owner;
        _dispatcher = dispatcher;
        Rating = ranking.Rate(descriptor);

        // Category winners are compared by folder name: two catalog entries of the
        // same family differ only by their variant folder.
        IsMostAccurate = Matches(ranking.MostAccurate);
        IsFastest = Matches(ranking.Fastest);
        IsSmallest = Matches(ranking.Smallest);
    }

    public ModelDescriptor Descriptor { get; }

    public ModelRating Rating { get; }

    /// <summary>The variant folder this model occupies under the models root.</summary>
    public string FolderName => Descriptor.SubfolderName;

    // ── Naming and description ─────────────────────────────────────────────

    public string Title => Descriptor.Title;

    public string PrecisionChip => Descriptor.PrecisionSummary;

    public string WindowChip => Descriptor.WindowSummary;

    public string Tagline => Descriptor.Tagline;

    public string Description => Descriptor.Description;

    // ── Measured properties ───────────────────────────────────────────────

    public string AccuracyText => Descriptor.AccuracyText;

    public string AccuracyDetailText => Descriptor.AccuracyDetailText;

    public string SpeedText => Descriptor.SpeedText;

    public string SizeText => Descriptor.SizeText;

    public string LatencyText => Descriptor.LatencyText;

    public string UseCaseText => Descriptor.UseCaseText;

    public string MetricsTooltip => Descriptor.MetricsLine;

    public string AccuracyStars => Rating.AccuracyStars;

    public string SpeedStars => Rating.SpeedStars;

    public string SizeStars => Rating.SizeStars;

    /// <summary>"Accuracy 5/5" style summary used by the card tooltip.</summary>
    public string RatingSummary =>
        $"Accuracy {Rating.Accuracy}/5 · Speed {Rating.Speed}/5 · Size {Rating.Size}/5";

    // ── Badges ────────────────────────────────────────────────────────────

    public bool ShowRecommendedBadge => Descriptor.IsRecommended;

    public bool IsMostAccurate { get; }

    public bool IsFastest { get; }

    public bool IsSmallest { get; }

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isInUse;

    public bool ShowInstalledBadge => IsInstalled;

    public bool ShowInUseBadge => IsInUse;

    /// <summary>False when the model is the only entry in its category (badge would be noise).</summary>
    public bool HasComparison => _owner.Ranking.RatedCount > 1;

    // ── Live download state ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(ActionText), nameof(IsIdle))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "";

    public bool CanDownload => !IsDownloading;

    /// <summary>Cards without a download in flight show the action buttons.</summary>
    public bool IsIdle => !IsDownloading;

    public string ActionText => IsDownloading ? $"Downloading {Progress:F0}%" : IsInstalled ? "Re-download" : "Download";

    public bool CanUse => IsInstalled && !IsDownloading && !IsInUse;

    public bool CanDelete => IsInstalled && !IsDownloading && !IsInUse;

    internal void ApplyState(bool installed, bool inUse)
    {
        IsInstalled = installed;
        IsInUse = inUse;
        OnPropertyChanged(nameof(ShowInstalledBadge));
        OnPropertyChanged(nameof(ShowInUseBadge));
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(ActionText));
    }

    /// <summary>Queues (or re-queues) this variant and reports progress on the card.</summary>
    internal async Task DownloadAsync(bool force)
    {
        if (IsDownloading)
            return;

        IsDownloading = true;
        Progress = 0;
        StatusText = "Queued...";

        var job = _owner.EnqueueDownload(Descriptor, force);
        _job = job;
        _jobHandler = updated =>
        {
            if (ReferenceEquals(updated, job))
                _dispatcher.TryEnqueue(() => UpdateFromJob(job));
        };
        _owner.Downloads.JobUpdated += _jobHandler;
        _owner.Downloads.JobFinished += _jobHandler;
        UpdateFromJob(job);

        try
        {
            await job.Completion.ConfigureAwait(false);
            _dispatcher.TryEnqueue(() => StatusText = "Downloaded");
            await _owner.OnDownloadCompletedAsync(this).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _dispatcher.TryEnqueue(() => StatusText = "Cancelled");
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"Failed: {ex.Message}");
        }
        finally
        {
            _owner.Downloads.JobUpdated -= _jobHandler;
            _owner.Downloads.JobFinished -= _jobHandler;
            _jobHandler = null;
            _job = null;
            _dispatcher.TryEnqueue(() =>
            {
                IsDownloading = false;
                Progress = 0;
            });
        }
    }

    internal Task UseAsync() => _owner.UseAsync(this);

    internal void Delete() => _owner.Delete(this);

    /// <summary>Cancels this card's queued/running download, if any.</summary>
    internal void CancelDownload()
    {
        if (_job is not null)
            _owner.Downloads.Cancel(_job);

        StatusText = "Cancelling...";
    }

    private void UpdateFromJob(DownloadJob job)
    {
        Progress = job.Percent;
        StatusText = job.State switch
        {
            DownloadJobState.Queued => "Queued",
            DownloadJobState.Downloading => job.FilesTotal > 0
                ? $"Downloading {job.CurrentFile} ({job.FilesDone}/{job.FilesTotal})"
                : "Downloading...",
            DownloadJobState.Completed => "Downloaded",
            DownloadJobState.Failed => $"Failed: {job.Error}",
            DownloadJobState.Cancelled => "Cancelled",
            _ => StatusText
        };
    }

    private bool Matches(ModelDescriptor? other)
        => other is not null && string.Equals(other.SubfolderName, Descriptor.SubfolderName, StringComparison.OrdinalIgnoreCase);
}
