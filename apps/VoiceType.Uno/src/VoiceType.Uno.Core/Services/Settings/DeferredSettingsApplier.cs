namespace VoiceType.Uno.Services;

/// <summary>
/// Applies Settings changes at once, except the ones that reload the speech model while a recording runs: those wait for
/// Stop (design §3.2). Every other change in the same edit goes through immediately. Changes are applied one at a time, each
/// on top of the settings in effect when its turn comes, so two quick edits never overwrite each other.
/// </summary>
public sealed class DeferredSettingsApplier
{
    private readonly Func<AppSettings, Task> _apply;
    private readonly Func<bool> _isRecording;
    private readonly Func<AppSettings> _live;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HeavyValues? _pending;

    /// <param name="apply">Applies a full settings object (MainViewModel.ApplySettingsAsync).</param>
    /// <param name="isRecording">True while a recording runs.</param>
    /// <param name="live">A copy of the settings in effect now.</param>
    public DeferredSettingsApplier(Func<AppSettings, Task> apply, Func<bool> isRecording, Func<AppSettings> live)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _isRecording = isRecording ?? throw new ArgumentNullException(nameof(isRecording));
        _live = live ?? throw new ArgumentNullException(nameof(live));
    }

    /// <summary>A model or compute change is waiting for Stop.</summary>
    public bool HasPending => _pending is not null;

    /// <summary>The compute the user chose that waits for Stop, or null.</summary>
    public string? PendingExecutionProvider => _pending?.ExecutionProvider;

    /// <summary>Raised when <see cref="HasPending"/> changes.</summary>
    public event Action? PendingChanged;

    /// <summary>Edits the settings in effect (plus a held-back model or compute choice) and applies the result.</summary>
    public async Task ChangeAsync(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var live = _live();
            var desired = live.Clone();
            _pending?.CopyTo(desired);
            mutate(desired);
            await SubmitCoreAsync(desired, live).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Call when the recording stopped: applies a held-back model or compute change.</summary>
    public async Task FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pending is not { } pending || _isRecording())
                return;
            var settings = _live();
            pending.CopyTo(settings);
            SetPending(null);
            await _apply(settings).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether changing <paramref name="before"/> into <paramref name="after"/> reloads the speech model.</summary>
    public static bool IsHeavy(AppSettings before, AppSettings after) => HeavyValues.From(before) != HeavyValues.From(after);

    private async Task SubmitCoreAsync(AppSettings desired, AppSettings live)
    {
        var wanted = HeavyValues.From(desired);
        if (!_isRecording() || wanted == HeavyValues.From(live))
        {
            SetPending(null);
            await _apply(desired).ConfigureAwait(false);
            return;
        }

        // Recording: everything else applies now, the model and compute stay as they are until Stop.
        SetPending(wanted);
        var now = desired.Clone();
        HeavyValues.From(live).CopyTo(now);
        await _apply(now).ConfigureAwait(false);
    }

    private void SetPending(HeavyValues? value)
    {
        var changed = (_pending is null) != (value is null);
        _pending = value;
        if (changed)
            PendingChanged?.Invoke();
    }

    /// <summary>What reloads the speech model from this page: the compute. (The model itself is switched on the Models page.)</summary>
    private readonly record struct HeavyValues(string ExecutionProvider)
    {
        public static HeavyValues From(AppSettings s) => new(SettingsText.NormalizeCompute(s.ExecutionProvider));

        public void CopyTo(AppSettings s) => s.ExecutionProvider = ExecutionProvider;
    }
}
