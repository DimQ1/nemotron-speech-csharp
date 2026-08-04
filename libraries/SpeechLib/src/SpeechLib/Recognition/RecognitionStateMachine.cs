namespace SpeechLib.Recognition;

/// <summary>
/// Explicit state machine for the speech recognition capture lifecycle.
/// Model loading is managed separately via <see cref="ModelState"/>.
/// </summary>
public enum RecognitionState
{
    Idle,
    Listening,
    Muted,
    Finalizing,
    Error
}

public enum RecognitionTrigger
{
    Start,
    Mute,
    Unmute,
    Stop,
    FlushDone,
    Reset
}

/// <summary>
/// State machine for managing recognition lifecycle transitions.
/// Thread-safe and validates all state transitions.
/// </summary>
public sealed class RecognitionStateMachine
{
    private readonly object _gate = new();

    public RecognitionState CurrentState { get; private set; } = RecognitionState.Idle;

    public bool CanFire(RecognitionTrigger trigger)
    {
        lock (_gate)
        {
            return CanFireInternal(trigger);
        }
    }

    public RecognitionState Fire(RecognitionTrigger trigger)
    {
        lock (_gate)
        {
            if (!CanFireInternal(trigger))
                throw new InvalidOperationException(
                    $"Invalid state transition: {CurrentState} -> {trigger}");

            CurrentState = (CurrentState, trigger) switch
            {
                (RecognitionState.Idle, RecognitionTrigger.Start) => RecognitionState.Listening,
                (RecognitionState.Error, RecognitionTrigger.Start) => RecognitionState.Listening,
                (RecognitionState.Listening, RecognitionTrigger.Start) => RecognitionState.Listening,
                (RecognitionState.Listening, RecognitionTrigger.Mute) => RecognitionState.Muted,
                (RecognitionState.Listening, RecognitionTrigger.Stop) => RecognitionState.Finalizing,
                (RecognitionState.Muted, RecognitionTrigger.Unmute) => RecognitionState.Listening,
                (RecognitionState.Muted, RecognitionTrigger.Stop) => RecognitionState.Finalizing,
                (RecognitionState.Finalizing, RecognitionTrigger.FlushDone) => RecognitionState.Idle,
                (_, RecognitionTrigger.Reset) => RecognitionState.Idle,
                _ => CurrentState
            };

            return CurrentState;
        }
    }

    private bool CanFireInternal(RecognitionTrigger trigger) =>
        (CurrentState, trigger) switch
        {
            (RecognitionState.Idle, RecognitionTrigger.Start) => true,
            (RecognitionState.Error, RecognitionTrigger.Start) => true,
            (RecognitionState.Listening, RecognitionTrigger.Start) => true,
            (RecognitionState.Listening, RecognitionTrigger.Mute) => true,
            (RecognitionState.Listening, RecognitionTrigger.Stop) => true,
            (RecognitionState.Muted, RecognitionTrigger.Unmute) => true,
            (RecognitionState.Muted, RecognitionTrigger.Stop) => true,
            (RecognitionState.Finalizing, RecognitionTrigger.FlushDone) => true,
            (_, RecognitionTrigger.Reset) => true,
            _ => false
        };

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return CurrentState is RecognitionState.Listening or RecognitionState.Muted;
            }
        }
    }
}
