namespace VoiceType.Uno.Services;

/// <summary>
/// Single owner of the live <see cref="AppSettings"/> instance and the only writer
/// of <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Before this type existed the app mutated one shared <see cref="AppSettings"/> from
/// the UI thread, from model initialization, from download continuations and from
/// settings-dialog apply, while <see cref="SettingsService.Update"/> performed its own
/// read-modify-write. A full <c>Save(snapshot)</c> racing an <c>Update</c> could drop
/// the other change. All mutations here run against the same instance and all writes
/// are serialized behind one gate, so a concurrent pair of mutations is persisted
/// together instead of one overwriting the other.
/// </remarks>
public sealed class SettingsStore
{
    private readonly SettingsService _service;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AppSettings _current;

    public SettingsStore(SettingsService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _current = service.Load();
    }

    /// <summary>The live settings instance. Mutate it only through this type.</summary>
    public AppSettings Current => _current;

    /// <summary>An independent copy, safe to hand to a modal dialog.</summary>
    public AppSettings Snapshot() => _current.Clone();

    /// <summary>
    /// Applies <paramref name="mutate"/> to the live instance, persists the result and
    /// returns the effective settings.
    /// </summary>
    public async Task<AppSettings> MutateAsync(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            mutate(_current);
            await WriteAsync().ConfigureAwait(false);
            return _current;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Persists the current settings under the write gate.</summary>
    public Task<AppSettings> SaveAsync() => MutateAsync(static _ => { });

    /// <summary>Replaces the live instance wholesale (settings dialog "Save").</summary>
    public async Task<AppSettings> ReplaceAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _current = settings;
            await WriteAsync().ConfigureAwait(false);
            return _current;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Applies a mutation to the live instance immediately (so subsequent reads see it)
    /// and persists it in the background. Never throws: both the mutation and the write
    /// report failures through <paramref name="onError"/> instead of surfacing as an
    /// exception on the UI thread or as an unobserved task exception.
    /// </summary>
    public void Post(Action<AppSettings> mutate, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        try
        {
            mutate(_current);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return;
        }

        _ = SaveInBackgroundAsync(onError);
    }

    private async Task SaveInBackgroundAsync(Action<Exception>? onError)
    {
        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }

    // SettingsService.Save already swallows I/O failures (it reports them through
    // Debug.WriteLine), so the file write itself never throws; it stays on the thread
    // pool because it is synchronous file I/O.
    private Task WriteAsync() => Task.Run(() => _service.Save(_current));
}
