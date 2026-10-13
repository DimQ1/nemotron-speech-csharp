using SpeechLib.Diarization;
using SpeechLib.ModelDownload;

namespace VoiceType.Uno.Services;

public enum DiarizationModelState { NotInstalled, Downloading, Installed, Active }

/// <summary>Which speaker-detection models are on disk or downloading, and which one the diarizer uses.</summary>
public interface IDiarizationModelService
{
    IReadOnlyList<DiarizationCatalogModel> Models { get; }

    DiarizationModelState StateOf(DiarizationCatalogModel model);

    /// <summary>0 to 100 while downloading, null otherwise.</summary>
    double? DownloadPercent(DiarizationCatalogModel model);

    /// <summary><c>42% · 44.0 / 105.9 MB</c> while downloading, null otherwise.</summary>
    string? DownloadProgressText(DiarizationCatalogModel model);

    /// <summary>The model the diarizer runs (the chosen one when installed, else the first installed); null when none of the catalog is.</summary>
    DiarizationCatalogModel? Active { get; }

    /// <summary>Some usable model is on disk: a catalog download or a folder the user copied (<c>diarization/</c> or the old flat root).</summary>
    bool IsModelAvailable { get; }

    /// <summary>A model is queued or downloading.</summary>
    bool IsDownloading { get; }

    /// <summary>Why the last download failed; null when the last one did not.</summary>
    string? LastError { get; }

    /// <summary>The model storage layout in use.</summary>
    ModelStorageLayout Layout { get; }

    /// <summary>Any state change (any thread).</summary>
    event Action? Changed;

    /// <summary>Queues the download into the shared manager; the model becomes installed after validation.</summary>
    void StartDownload(DiarizationCatalogModel model);

    void CancelDownload(DiarizationCatalogModel model);

    /// <summary>Makes an installed model the one the diarizer uses (INT8 or FP32); persisted in the settings.</summary>
    Task SelectAsync(DiarizationCatalogModel model, CancellationToken cancellationToken = default);

    /// <summary>Removes the model folder (cancels its download first).</summary>
    void Delete(DiarizationCatalogModel model);
}

/// <summary>
/// <see cref="IDiarizationModelService"/> over the app-wide <see cref="ModelDownloadManager"/> and
/// <see cref="SettingsStore"/> (the chosen model is <c>History.Speakers.ModelFolder</c>).
/// </summary>
public sealed class DiarizationModelService : IDiarizationModelService, IDisposable
{
    private readonly ModelDownloadManager _downloads;
    private readonly Func<ModelStorageLayout> _layout;
    private readonly Func<string?> _preferredFolder;
    private readonly Func<string?, Task> _savePreferredFolder;
    private readonly bool _isPhone;
    private readonly object _lock = new();
    private string? _lastError;

    public DiarizationModelService(
        ModelDownloadManager downloads,
        Func<ModelStorageLayout>? layout = null,
        Func<string?>? preferredFolder = null,
        Func<string?, Task>? savePreferredFolder = null,
        bool isPhone = false)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _layout = layout ?? (() => ModelStorageLayout.Current);
        _preferredFolder = preferredFolder ?? (() => null);
        _savePreferredFolder = savePreferredFolder ?? (_ => Task.CompletedTask);
        _isPhone = isPhone;

        _downloads.JobAdded += OnJob;
        _downloads.JobUpdated += OnJob;
        _downloads.JobFinished += OnJob;
        _downloads.JobRemoved += OnJob;
    }

    /// <summary>Wires the service to the settings store: the chosen folder lives in <c>History.Speakers.ModelFolder</c>.</summary>
    public static DiarizationModelService ForSettings(ModelDownloadManager downloads, SettingsStore settings, bool isPhone) =>
        new(downloads,
            () => ModelStorageLayout.Resolve(settings.Current),
            () => settings.Current.History.Speakers.ModelFolder,
            folder => settings.MutateAsync(s => s.History.Speakers.ModelFolder = folder),
            isPhone);

    public event Action? Changed;

    public IReadOnlyList<DiarizationCatalogModel> Models => DiarizationModelCatalog.ForDevice(_isPhone);

    public ModelStorageLayout Layout => _layout();

    public string? LastError
    {
        get
        {
            lock (_lock)
                return _lastError;
        }
    }

    public bool IsDownloading => Models.Any(m => _downloads.FindActive(DiarizationModelCatalog.DownloadKey(m)) is not null);

    public bool IsModelAvailable => DiarizationModels.Installed(_layout()).Count > 0;

    public DiarizationCatalogModel? Active
    {
        get
        {
            var layout = _layout();
            var chosen = DiarizationModelCatalog.FindByFolder(_preferredFolder());
            if (chosen is not null && chosen.IsInstalledIn(layout))
                return chosen;
            return Models.FirstOrDefault(m => m.IsInstalledIn(layout));
        }
    }

    public DiarizationModelState StateOf(DiarizationCatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.IsInstalledIn(_layout()))
            return Active is { } active && active.Id == model.Id ? DiarizationModelState.Active : DiarizationModelState.Installed;
        return _downloads.FindActive(DiarizationModelCatalog.DownloadKey(model)) is not null
            ? DiarizationModelState.Downloading
            : DiarizationModelState.NotInstalled;
    }

    public double? DownloadPercent(DiarizationCatalogModel model) =>
        _downloads.FindActive(DiarizationModelCatalog.DownloadKey(model ?? throw new ArgumentNullException(nameof(model))))?.Percent;

    public string? DownloadProgressText(DiarizationCatalogModel model)
    {
        var job = _downloads.FindActive(DiarizationModelCatalog.DownloadKey(model ?? throw new ArgumentNullException(nameof(model))));
        if (job is null)
            return null;
        return job.TotalBytes > 0
            ? ModelsPageFormatter.DescribeProgress(job.Percent, job.DownloadedBytes, job.TotalBytes)
            : "Starting…";
    }

    public void StartDownload(DiarizationCatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var layout = _layout();
        if (model.IsInstalledIn(layout) || _downloads.FindActive(DiarizationModelCatalog.DownloadKey(model)) is not null)
            return;

        lock (_lock)
            _lastError = null;
        _downloads.Enqueue(CreateRequest(model, layout));
        RaiseChanged();
    }

    public void CancelDownload(DiarizationCatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_downloads.FindActive(DiarizationModelCatalog.DownloadKey(model)) is { } job)
            _downloads.Cancel(job);
        RaiseChanged();
    }

    public async Task SelectAsync(DiarizationCatalogModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsInstalledIn(_layout()))
            throw new InvalidOperationException($"{model.Title} is not downloaded.");
        if (_isPhone && !model.PhoneSupported)
            throw new InvalidOperationException($"{model.Title} is too large for a phone.");

        await _savePreferredFolder(model.Folder).ConfigureAwait(false);
        RaiseChanged();
    }

    public void Delete(DiarizationCatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        CancelDownload(model);
        try
        {
            var dir = _layout().DiarizationModelPath(model.Folder);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_lock)
                _lastError = $"Could not delete {model.Title}: {ex.Message}";
        }
        RaiseChanged();
    }

    /// <summary>The download of a model: the repo's <c>int8/</c> or <c>fp32/</c> folder into <c>&lt;root&gt;/diarization/&lt;Folder&gt;</c>.</summary>
    public static ModelDownloadRequest CreateRequest(DiarizationCatalogModel model, ModelStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);
        return new ModelDownloadRequest(
            DiarizationModelCatalog.DownloadKey(model),
            $"Speaker detection · {model.Title}",
            model.RepoId,
            layout.DiarizationModelPath(model.Folder))
        {
            Subfolder = model.RepoSubfolder,
            Kind = ModelDownloadKind.Diarization,
            // A truncated or wrong download fails the job instead of leaving a folder that looks installed.
            AfterDownload = folder => Finalize(model, folder),
        };
    }

    /// <summary>Validates the downloaded folder and leaves the attribution next to the weights.</summary>
    internal static void Finalize(DiarizationCatalogModel model, string folder)
    {
        DiarizationModelLocator.Validate(folder, model.Precision);
        File.WriteAllText(Path.Combine(folder, "NOTICE.txt"),
            $"{model.Title}{Environment.NewLine}ONNX export of {DiarizationModelLocator.UpstreamRepo}.{Environment.NewLine}"
            + $"License: {DiarizationModelLocator.License}.{Environment.NewLine}Source: https://huggingface.co/{model.RepoId}{Environment.NewLine}");
    }

    public void Dispose()
    {
        _downloads.JobAdded -= OnJob;
        _downloads.JobUpdated -= OnJob;
        _downloads.JobFinished -= OnJob;
        _downloads.JobRemoved -= OnJob;
    }

    private void OnJob(DownloadJob job)
    {
        var model = DiarizationModelCatalog.All.FirstOrDefault(m =>
            string.Equals(DiarizationModelCatalog.DownloadKey(m), job.Key, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return;

        if (job.State == DownloadJobState.Failed)
        {
            lock (_lock)
                _lastError = $"Download of {model.Title} failed: {job.Error}";
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A subscriber failure must not break downloads.
        }
    }
}

/// <summary>Everything a speaker-model card shows, computed from the service state (no UI types).</summary>
public sealed record DiarizationUiCard(
    DiarizationCatalogModel Model,
    DiarizationModelState State,
    IReadOnlyList<LlmUiChip> Chips,
    string StatusText,
    LlmUiAction Action,
    string ActionText,
    bool ActionVisible,
    bool ShowProgress,
    double Percent,
    string ProgressText,
    bool CanDelete);

public static class DiarizationUiCardBuilder
{
    public static DiarizationUiCard Build(DiarizationCatalogModel model, DiarizationModelState state, double? percent, string? progressText)
    {
        ArgumentNullException.ThrowIfNull(model);
        var chips = new List<LlmUiChip>
        {
            new(DownloadProgressFormatter.FormatBytes(model.SizeBytes), LlmUiChipKind.Plain),
            new(model.Precision == DiarizationPrecision.Int8 ? "INT8" : "FP32", LlmUiChipKind.Plain),
        };
        if (model.IsRecommended)
            chips.Add(new("★ Recommended", LlmUiChipKind.Recommended));
        if (!model.PhoneSupported)
            chips.Add(new("Desktop only", LlmUiChipKind.Plain));
        if (state == DiarizationModelState.Active)
            chips.Add(new("● In use", LlmUiChipKind.InUse));
        else if (state == DiarizationModelState.Installed)
            chips.Add(new("installed", LlmUiChipKind.Plain));

        var status = state switch
        {
            DiarizationModelState.Downloading => "Downloading…",
            DiarizationModelState.Active => "In use for speaker detection",
            DiarizationModelState.Installed => "Installed",
            _ => "Not downloaded",
        };
        var (action, text) = state switch
        {
            DiarizationModelState.Downloading => (LlmUiAction.Cancel, "Cancel"),
            DiarizationModelState.Installed => (LlmUiAction.Use, "Use"),
            DiarizationModelState.Active => (LlmUiAction.InUse, "In use"),
            _ => (LlmUiAction.Download, "Download"),
        };
        return new DiarizationUiCard(model, state, chips, status, action, text,
            ActionVisible: action != LlmUiAction.InUse,
            ShowProgress: state == DiarizationModelState.Downloading,
            Percent: Math.Clamp(percent ?? 0, 0, 100),
            ProgressText: progressText ?? "",
            CanDelete: state is DiarizationModelState.Installed or DiarizationModelState.Active);
    }
}
