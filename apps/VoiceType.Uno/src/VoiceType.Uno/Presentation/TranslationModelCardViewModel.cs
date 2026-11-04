using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// The Translation tab of the Models page: the on-device Gemma 4 E2B (LiteRT-LM)
/// model with its install state and the job for <see cref="TranslationModelInfo.Key"/>
/// in the shared <see cref="ModelDownloadManager"/>. Downloads go through the main
/// view model so the main screen's "translation model missing" banner stays in step.
/// </summary>
public sealed partial class TranslationModelCardViewModel : ObservableObject
{
    private readonly ModelDownloadManager _downloads;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _enqueueDownload;

    public TranslationModelCardViewModel(
        ModelDownloadManager downloads,
        DispatcherQueue dispatcher,
        Action enqueueDownload)
    {
        _downloads = downloads;
        _dispatcher = dispatcher;
        _enqueueDownload = enqueueDownload;

        _downloads.JobAdded += OnJobChanged;
        _downloads.JobUpdated += OnJobChanged;
        _downloads.JobFinished += OnJobChanged;
        _downloads.JobRemoved += OnJobChanged;
        Refresh();
    }

    public string Title => "Gemma 4 E2B";

    public string FileText => $"{TranslationModelInfo.FileName} · {TranslationModelInfo.RepoId[..TranslationModelInfo.RepoId.IndexOf('/')]}";

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private bool _isProgressVisible;

    /// <summary>Installed / Partial download / Not downloaded / Queued / Downloading x%.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>"1.0 / 2.4 GB" while downloading, the size on disk once installed.</summary>
    [ObservableProperty]
    private string _detailText = "";

    [ObservableProperty]
    private string _rateText = "";

    [ObservableProperty]
    private bool _canDownload;

    [ObservableProperty]
    private string _downloadText = Loc.T("Models.Card.Action.Download");

    [ObservableProperty]
    private bool _canDelete;

    /// <summary>Re-reads the file on disk and the queue state.</summary>
    public void Refresh()
    {
        var job = _downloads.FindActive(TranslationModelInfo.Key)
            ?? _downloads.Jobs.LastOrDefault(j => string.Equals(j.Key, TranslationModelInfo.Key, StringComparison.OrdinalIgnoreCase));
        var active = job is { IsActive: true };
        var installed = TranslationModelInfo.IsDownloaded;
        var partial = !installed && TranslationModelInfo.HasPartialDownload;

        IsInstalled = installed;
        IsDownloading = active;
        IsProgressVisible = active && job!.State == DownloadJobState.Downloading;
        Progress = active ? job!.Percent : 0;
        IsProgressIndeterminate = active && job!.TotalBytes == 0;
        RateText = active && job!.State == DownloadJobState.Downloading
            ? ModelsPageFormatter.DescribeRate(job.BytesPerSecond, job.DownloadedBytes, job.TotalBytes)
            : "";

        StatusText = (active, job?.State) switch
        {
            (true, DownloadJobState.Queued) => Loc.T("Models.Translation.Status.Queued"),
            (true, _) => job!.TotalBytes > 0 ? Loc.F("Models.Translation.Status.DownloadingPercent", job.Percent) : Loc.T("Models.Card.Status.Downloading"),
            _ when installed => Loc.T("Models.Card.Status.Installed"),
            (_, DownloadJobState.Failed) => Loc.F("Models.Translation.Status.Failed", job!.Error),
            _ when partial => Loc.T("Models.Translation.Status.Partial"),
            _ => Loc.T("Models.Card.Status.NotDownloaded")
        };

        DetailText = active
            ? ModelsPageFormatter.DescribeTransfer(job!.DownloadedBytes, job.TotalBytes)
            : installed
                ? DownloadProgressFormatter.FormatBytes(ModelStorage.SizeOf(TranslationModelInfo.LocalModelPath))
                : "";

        CanDownload = !active && !installed;
        DownloadText = partial || job?.State is DownloadJobState.Failed or DownloadJobState.Cancelled
            ? Loc.T("Models.Card.Action.Retry")
            : Loc.T("Models.Card.Action.Download");
        CanDelete = installed && !active;
    }

    /// <summary>Queues the download through the main view model (keeps its banner in step).</summary>
    public void Download()
    {
        _enqueueDownload();
        Refresh();
    }

    public void Cancel()
    {
        if (_downloads.FindActive(TranslationModelInfo.Key) is { } job)
            _downloads.Cancel(job);
    }

    /// <summary>Deletes the model file; reports when it is still in use and could not be removed.</summary>
    public void Delete()
    {
        TranslationModelInfo.DeleteDownloaded();
        Refresh();

        if (TranslationModelInfo.IsDownloaded)
            StatusText = Loc.T("Models.Translation.CouldNotDeleteInUse");
    }

    /// <summary>Unsubscribes from the download manager when the page closes.</summary>
    public void Detach()
    {
        _downloads.JobAdded -= OnJobChanged;
        _downloads.JobUpdated -= OnJobChanged;
        _downloads.JobFinished -= OnJobChanged;
        _downloads.JobRemoved -= OnJobChanged;
    }

    private void OnJobChanged(DownloadJob job)
    {
        if (string.Equals(job.Key, TranslationModelInfo.Key, StringComparison.OrdinalIgnoreCase))
            _dispatcher.TryEnqueue(Refresh);
    }
}
