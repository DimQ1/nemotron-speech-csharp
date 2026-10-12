using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Services;
namespace VoiceType.Uno.Presentation;

/// <summary>
/// One catalog model shown as a card on the Models page. Exposes the
/// human-readable naming and metrics from <see cref="ModelDescriptor"/> plus the
/// live state (installed / in use / downloading) needed to pick a model, and
/// the single primary action that state allows (Download → Cancel → Use → In use).
/// </summary>
public sealed partial class ModelCardViewModel : ObservableObject
{
    private readonly ModelManagerViewModel _owner;
    private readonly DispatcherQueue _dispatcher;
    private DownloadJob? _job;
    private Action<DownloadJob>? _jobHandler;

    /// <summary>Outcome of the last download (failed / cancelled), shown until the state changes again.</summary>
    private string _outcome = "";

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

    /// <summary>"Accuracy 5/5" style summary shown with the details.</summary>
    public string RatingSummary =>
        $"Accuracy {Rating.Accuracy}/5 · Speed {Rating.Speed}/5 · Size {Rating.Size}/5";

    // ── Badges ────────────────────────────────────────────────────────────

    public bool ShowRecommendedBadge => Descriptor.IsRecommended;

    public bool IsMostAccurate { get; }

    public bool IsFastest { get; }

    public bool IsSmallest { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstalledBadge), nameof(ShowDownloadAction), nameof(ShowSecondaryAction),
        nameof(SecondaryActionText), nameof(IsSecondaryActionEnabled), nameof(StateText), nameof(CanUse),
        nameof(CanDelete), nameof(CanRedownload))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInUseBadge), nameof(ShowInstalledBadge), nameof(SecondaryActionText),
        nameof(IsSecondaryActionEnabled), nameof(CanUse), nameof(CanDelete))]
    private bool _isInUse;

    /// <summary>"installed" is implied by "● In use", so only one of the two chips shows.</summary>
    public bool ShowInstalledBadge => IsInstalled && !IsInUse;

    public bool ShowInUseBadge => IsInUse;

    /// <summary>Expanded "Details" section (per-language WER, latency, use case).</summary>
    [ObservableProperty]
    private bool _isDetailsExpanded;

    // ── Live download state ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadAction), nameof(ShowSecondaryAction), nameof(SecondaryActionText),
        nameof(IsSecondaryActionEnabled), nameof(StateText), nameof(CanUse), nameof(CanDelete), nameof(CanRedownload))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    /// <summary>True while the job waits for a slot or has no size yet.</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>"42% · 1.0 / 2.4 GB" under the progress bar.</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>"3.1 MB/s · 7 min" under the progress bar.</summary>
    [ObservableProperty]
    private string _rateText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string _statusText = "";

    /// <summary>Short state shown left of the actions: Installed / Queued / Downloading… / Failed: ….</summary>
    public string StateText => IsDownloading
        ? StatusText
        : _outcome.Length > 0
            ? _outcome
            : IsInstalled ? "Installed" : "";

    // ── Actions: one primary action by state + the "⋯" menu ──────────────

    /// <summary>Not installed and idle: the accent "Download" button.</summary>
    public bool ShowDownloadAction => !IsDownloading && !IsInstalled;

    /// <summary>Otherwise a plain button: Cancel / Use / In use.</summary>
    public bool ShowSecondaryAction => !ShowDownloadAction;

    public string SecondaryActionText => IsDownloading ? "Cancel" : IsInUse ? "In use" : "Use";

    public bool IsSecondaryActionEnabled => IsDownloading || CanUse;

    public bool CanUse => IsInstalled && !IsDownloading && !IsInUse;

    public bool CanDelete => IsInstalled && !IsDownloading && !IsInUse;

    public bool CanRedownload => IsInstalled && !IsDownloading;

    internal void ApplyState(bool installed, bool inUse)
    {
        IsInstalled = installed;
        IsInUse = inUse;
    }

    /// <summary>
    /// Shows the progress of a download for this model that is already queued or
    /// running (started earlier from this page, or still running after the page closed).
    /// The card that enqueued the job switches the active model; this one only reports.
    /// </summary>
    internal void AttachToActiveJob()
    {
        if (_job is not null)
            return;

        // The main screen's "model missing" banner queues the default model under its own key.
        var job = _owner.Downloads.FindActive(FolderName)
            ?? (string.Equals(FolderName, DefaultAsrModel.FolderName, StringComparison.OrdinalIgnoreCase)
                ? _owner.Downloads.FindActive(DefaultAsrModel.Key)
                : null);
        if (job is null)
            return;

        _ = ObserveAsync(job, adoptOnCompletion: false);
    }

    /// <summary>Queues (or re-queues) this variant and reports progress on the card.</summary>
    internal async Task DownloadAsync(bool force)
    {
        if (IsDownloading)
            return;

        var job = _owner.EnqueueDownload(Descriptor, force);
        await ObserveAsync(job, adoptOnCompletion: true).ConfigureAwait(false);
    }

    private async Task ObserveAsync(DownloadJob job, bool adoptOnCompletion)
    {
        _outcome = "";
        IsDownloading = true;
        Progress = 0;
        StatusText = "Queued";

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
            if (adoptOnCompletion)
                await _owner.OnDownloadCompletedAsync(this).ConfigureAwait(false);
            else
                _dispatcher.TryEnqueue(_owner.Refresh);
        }
        catch (OperationCanceledException)
        {
            _dispatcher.TryEnqueue(() => _outcome = "Cancelled");
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => _outcome = $"Failed: {ex.Message}");
        }
        finally
        {
            DetachJobHandler();
            _dispatcher.TryEnqueue(() =>
            {
                _job = null;
                IsDownloading = false;
                Progress = 0;
                ProgressText = "";
                RateText = "";
                OnPropertyChanged(nameof(StateText));
            });
        }
    }

    /// <summary>Stops progress updates (the page closed); a running job keeps downloading.</summary>
    internal void Detach() => DetachJobHandler();

    private void DetachJobHandler()
    {
        if (_jobHandler is null)
            return;

        _owner.Downloads.JobUpdated -= _jobHandler;
        _owner.Downloads.JobFinished -= _jobHandler;
        _jobHandler = null;
    }

    internal Task UseAsync() => _owner.UseAsync(this);

    internal void Delete() => _owner.Delete(this);

    /// <summary>Cancels this card's queued/running download, if any.</summary>
    internal void CancelDownload()
    {
        if (_job is not null)
            _owner.Downloads.Cancel(_job);

        StatusText = "Cancelling…";
    }

    private void UpdateFromJob(DownloadJob job)
    {
        Progress = job.Percent;
        IsProgressIndeterminate = job.State == DownloadJobState.Queued || job.TotalBytes == 0;
        ProgressText = ModelsPageFormatter.DescribeProgress(job.Percent, job.DownloadedBytes, job.TotalBytes);
        RateText = job.State == DownloadJobState.Downloading
            ? ModelsPageFormatter.DescribeRate(job.BytesPerSecond, job.DownloadedBytes, job.TotalBytes)
            : "";
        StatusText = job.State switch
        {
            DownloadJobState.Queued => "Queued",
            DownloadJobState.Downloading => "Downloading…",
            DownloadJobState.Completed => "Downloaded",
            DownloadJobState.Failed => $"Failed: {job.Error}",
            DownloadJobState.Cancelled => "Cancelled",
            _ => StatusText
        };
    }

    private bool Matches(ModelDescriptor? other)
        => other is not null && string.Equals(other.SubfolderName, Descriptor.SubfolderName, StringComparison.OrdinalIgnoreCase);
}
