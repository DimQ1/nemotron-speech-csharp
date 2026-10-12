using SpeechLib.Llm;
using SpeechLib.ModelDownload;

namespace VoiceType.Uno.Services;

/// <summary>
/// Which language model the shared engine runs, which are on disk or downloading, and the CPU/GPU choice.
/// Downloads go through the app-wide <see cref="ModelDownloadManager"/>; switching reconfigures the one shared
/// engine (<see cref="ILlmHostConfiguration"/>) and persists through <see cref="SettingsStore"/>.
/// </summary>
public sealed class LlmModelService : ILlmModelService, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ModelDownloadManager _downloads;
    private readonly ILlmHostConfiguration _host;
    private readonly IReadOnlyList<LlmModelInfo> _models;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _lock = new();
    private LlmModelInfo _active;
    private string _backend;

    public LlmModelService(
        SettingsStore settings,
        ModelDownloadManager downloads,
        ILlmHostConfiguration host,
        IReadOnlyList<LlmModelInfo>? models = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _models = models ?? LlmModelCatalog.All;

        _active = Resolve(_settings.Current.LlmModelId);
        _backend = NormalizeBackend(_settings.Current.TranslationComputeBackend);

        _downloads.JobAdded += OnJob;
        _downloads.JobUpdated += OnJob;
        _downloads.JobFinished += OnJob;
        _downloads.JobRemoved += OnJob;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public LlmModelInfo Active
    {
        get
        {
            lock (_lock)
                return _active;
        }
    }

    /// <inheritdoc />
    public string ComputeBackend
    {
        get
        {
            lock (_lock)
                return _backend;
        }
    }

    /// <inheritdoc />
    public LlmModelState StateOf(LlmModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.IsInstalled)
            return SameModel(model, Active) ? LlmModelState.Active : LlmModelState.Installed;
        return _downloads.FindActive(KeyOf(model)) is not null ? LlmModelState.Downloading : LlmModelState.NotInstalled;
    }

    /// <inheritdoc />
    /// <remarks>0 to 100.</remarks>
    public double? DownloadPercent(LlmModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return _downloads.FindActive(KeyOf(model))?.Percent;
    }

    /// <inheritdoc />
    public void StartDownload(LlmModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.IsInstalled || _downloads.FindActive(KeyOf(model)) is not null)
            return;

        _downloads.Enqueue(CreateRequest(model));
        RaiseChanged();
    }

    /// <inheritdoc />
    public void CancelDownload(LlmModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_downloads.FindActive(KeyOf(model)) is { } job)
            _downloads.Cancel(job);
        RaiseChanged();
    }

    /// <inheritdoc />
    public async Task SelectAsync(LlmModelInfo model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsInstalled)
            throw new InvalidOperationException($"{model.Title} is not downloaded.");
        if (OperatingSystem.IsAndroid() && !model.PhoneSupported)
            throw new InvalidOperationException($"{model.Title} is too large for a phone.");

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _host.ReconfigureAsync(new LlmHostSettings(model.LocalPath, ComputeBackend, model.ContextTokens), cancellationToken)
                .ConfigureAwait(false);
            lock (_lock)
                _active = model;
            await _settings.MutateAsync(s => s.LlmModelId = model.Id).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }

        RaiseChanged();
    }

    /// <inheritdoc />
    public async Task SetComputeBackendAsync(string backend, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBackend(backend);

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = Active;
            await _host.ReconfigureAsync(new LlmHostSettings(active.LocalPath, normalized, active.ContextTokens), cancellationToken)
                .ConfigureAwait(false);
            lock (_lock)
                _backend = normalized;
            await _settings.MutateAsync(s => s.TranslationComputeBackend = normalized).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }

        RaiseChanged();
    }

    /// <inheritdoc />
    public void Delete(LlmModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (SameModel(model, Active))
            throw new InvalidOperationException($"{model.Title} is the active model; switch to another model first.");

        CancelDownload(model);
        DeleteFiles(model);
        RaiseChanged();
    }

    public void Dispose()
    {
        _downloads.JobAdded -= OnJob;
        _downloads.JobUpdated -= OnJob;
        _downloads.JobFinished -= OnJob;
        _downloads.JobRemoved -= OnJob;
        _switchGate.Dispose();
    }

    /// <summary>The download request of a model: one file from its repo into the models folder.</summary>
    public static ModelDownloadRequest CreateRequest(LlmModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ModelDownloadRequest(
            LlmModelCatalog.DownloadKey(model),
            $"Language model · {model.Title}",
            model.RepoId,
            Path.GetDirectoryName(model.LocalPath) ?? AppPaths.ModelsDir)
        {
            SingleFile = Path.GetFileName(model.LocalPath),
            Kind = ModelDownloadKind.Translation,
        };
    }

    /// <summary>Normalizes the persisted compute backend to <c>"cpu"</c> or <c>"gpu"</c>.</summary>
    public static string NormalizeBackend(string? backend) =>
        string.Equals(backend?.Trim(), "gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "cpu";

    private LlmModelInfo Resolve(string? selectedId)
    {
        var selected = _models.FirstOrDefault(m => string.Equals(m.Id, selectedId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (selected is not null && selected.IsInstalled && (!OperatingSystem.IsAndroid() || selected.PhoneSupported))
            return selected;

        var fallback = _models[0];
        if (fallback.IsInstalled)
            return fallback;
        return _models.FirstOrDefault(m => m.IsInstalled && (!OperatingSystem.IsAndroid() || m.PhoneSupported)) ?? fallback;
    }

    private static bool SameModel(LlmModelInfo a, LlmModelInfo b) =>
        string.Equals(a.Id, b.Id, StringComparison.Ordinal);

    private static string KeyOf(LlmModelInfo model) => LlmModelCatalog.DownloadKey(model);

    private static void DeleteFiles(LlmModelInfo model)
    {
        var path = model.LocalPath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        // The model, an interrupted download (.part/.download) and the engine's weight caches next to it
        // (<file>_<stamp>_<size>.xnnpack_cache, *_mldrift_*.bin).
        var prefix = Path.GetFileName(path);
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a locked cache file is removed with the next delete.
            }
        }
    }

    private void OnJob(DownloadJob job)
    {
        foreach (var model in _models)
        {
            if (string.Equals(job.Key, KeyOf(model), StringComparison.OrdinalIgnoreCase))
            {
                RaiseChanged();
                return;
            }
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A subscriber failure must not break downloads or switching.
        }
    }
}
