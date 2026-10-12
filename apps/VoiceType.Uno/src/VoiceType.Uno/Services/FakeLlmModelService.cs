#if DEBUG
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Services;

/// <summary>
/// Debug-only stand-in for the real <see cref="ILlmModelService"/> (registered only when none exists):
/// keeps the model states in memory, simulates a ~12 s download with progress and a ~2 s load,
/// and touches no files. Lets the Models page be checked without 7 GB of downloads.
/// </summary>
public sealed class FakeLlmModelService : ILlmModelService
{
    private readonly object _gate = new();
    private readonly HashSet<string> _installed = new() { LlmModelCatalog.Default.Id };
    private readonly Dictionary<string, double> _downloading = new();
    private readonly Dictionary<string, CancellationTokenSource> _cancels = new();
    private LlmModelInfo _active = LlmModelCatalog.Default;
    private string _backend = "cpu";

    public LlmModelInfo Active => _active;

    public string ComputeBackend => _backend;

    public event Action? Changed;

    public LlmModelState StateOf(LlmModelInfo model)
    {
        lock (_gate)
        {
            if (_downloading.ContainsKey(model.Id))
                return LlmModelState.Downloading;
            if (!_installed.Contains(model.Id))
                return LlmModelState.NotInstalled;
            return model.Id == _active.Id ? LlmModelState.Active : LlmModelState.Installed;
        }
    }

    public double? DownloadPercent(LlmModelInfo model)
    {
        lock (_gate)
            return _downloading.TryGetValue(model.Id, out var p) ? p : null;
    }

    public void StartDownload(LlmModelInfo model)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_installed.Contains(model.Id) || _downloading.ContainsKey(model.Id))
                return;
            _downloading[model.Id] = 0;
            _cancels[model.Id] = cts;
        }

        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                for (var step = 1; step <= 60; step++)
                {
                    await Task.Delay(200, cts.Token).ConfigureAwait(false);
                    lock (_gate)
                        _downloading[model.Id] = step / 60d * 100;
                    Changed?.Invoke();
                }

                lock (_gate)
                {
                    _installed.Add(model.Id);
                    _downloading.Remove(model.Id);
                    _cancels.Remove(model.Id);
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _downloading.Remove(model.Id);
                    _cancels.Remove(model.Id);
                }
            }

            Changed?.Invoke();
        });
    }

    public void CancelDownload(LlmModelInfo model)
    {
        lock (_gate)
        {
            if (_cancels.TryGetValue(model.Id, out var cts))
                cts.Cancel();
        }
    }

    public async Task SelectAsync(LlmModelInfo model, CancellationToken cancellationToken = default)
    {
        if (StateOf(model) is LlmModelState.NotInstalled or LlmModelState.Downloading)
            throw new InvalidOperationException($"{model.Title} is not installed.");

        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        if (model.MinFreeRamGb >= 12 && _backend == "cpu" && Environment.GetEnvironmentVariable("VOICETYPE_FAKE_LLM_OOM") == "1")
            throw new OutOfMemoryException();

        lock (_gate)
            _active = model;
        Changed?.Invoke();
    }

    public async Task SetComputeBackendAsync(string backend, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        _backend = backend.Equals("gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "cpu";
        Changed?.Invoke();
    }

    public void Delete(LlmModelInfo model)
    {
        lock (_gate)
        {
            if (model.Id == _active.Id)
                throw new InvalidOperationException("The model in use cannot be deleted.");
            _installed.Remove(model.Id);
        }

        Changed?.Invoke();
    }
}
#endif
