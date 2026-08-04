namespace LVA.App.Companion;

/// <summary>Pet states per TZ §14.3 — full mandatory set.</summary>
public enum CompanionState
{
    Sleeping,
    Idle,
    Listening,
    Recognizing,
    Activated,
    Thinking,
    Working,
    Success,
    Error,
    Cancelled,
    Speaking,
    // Interactive (user-driven)
    Curious,
    Petting,
    Poked,
    Carried,
    Greeting
}

/// <summary>
/// Companion state machine (TZ §14.3): subscribes to core events and maps them
/// to pet states. Interactive states override temporarily and fall back.
/// </summary>
public sealed class CompanionStateMachine
{
    private CompanionState _baseState = CompanionState.Idle;
    private CompanionState? _overrideState;

    public CompanionState Current => _overrideState ?? _baseState;

    public event Action<CompanionState>? StateChanged;

    public void SetBase(CompanionState state)
    {
        _baseState = state;
        if (_overrideState is null)
            StateChanged?.Invoke(Current);
    }

    /// <summary>Overrides the state temporarily (interactive reactions), then reverts.</summary>
    public async Task OverrideForAsync(CompanionState state, TimeSpan duration, CancellationToken ct = default)
    {
        _overrideState = state;
        StateChanged?.Invoke(Current);
        try
        {
            await Task.Delay(duration, ct);
        }
        catch (OperationCanceledException) { }
        _overrideState = null;
        StateChanged?.Invoke(Current);
    }

    // ── Core-event mappings (TZ §14.3 table) ──────────────────────────

    public void OnMicrophoneMuted() => SetBase(CompanionState.Sleeping);
    public void OnMicrophoneActive() => SetBase(CompanionState.Idle);
    public void OnSpeechStarted() => SetBase(CompanionState.Listening);
    public void OnPartialTranscript() => SetBase(CompanionState.Recognizing);
    public void OnActivated() => SetBase(CompanionState.Activated);
    public void OnQuestionReady() => SetBase(CompanionState.Thinking);
    public void OnTaskStarted() => SetBase(CompanionState.Working);
    public void OnTaskCompleted() => SetBase(CompanionState.Success);
    public void OnTaskFailed() => SetBase(CompanionState.Error);
    public void OnCancelled() => SetBase(CompanionState.Cancelled);
    public void OnSpeaking() => SetBase(CompanionState.Speaking);
    public void OnSpeakingEnded() => SetBase(CompanionState.Idle);
    public void OnAppStarted() => SetBase(CompanionState.Greeting);
}
