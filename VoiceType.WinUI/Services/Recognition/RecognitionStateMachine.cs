namespace VoiceType.WinUI.Services.Recognition;

/// <summary>
/// Explicit state machine for the speech recognition lifecycle.
/// Replaces scattered boolean flags with guaranteed valid state transitions.
/// </summary>
public enum RecognitionState
{
    Idle,
    Initializing,
    Listening,
    Muted,
    Finalizing,
    Error
}

public enum RecognitionTrigger
{
    Start,
    InitOk,
    InitFail,
    Mute,
    Unmute,
    Stop,
    FlushDone,
    Reset
}

public sealed class RecognitionStateMachine
{
    public RecognitionState CurrentState { get; private set; } = RecognitionState.Idle;

    public bool CanFire(RecognitionTrigger trigger) =>
        (CurrentState, trigger) switch
        {
            (RecognitionState.Idle, RecognitionTrigger.Start) => true,
            (RecognitionState.Error, RecognitionTrigger.Start) => true,
            (RecognitionState.Listening, RecognitionTrigger.Start) => true,
            (RecognitionState.Initializing, RecognitionTrigger.InitOk) => true,
            (RecognitionState.Initializing, RecognitionTrigger.InitFail) => true,
            (RecognitionState.Listening, RecognitionTrigger.InitOk) => true,
            (RecognitionState.Listening, RecognitionTrigger.InitFail) => true,
            (RecognitionState.Listening, RecognitionTrigger.Mute) => true,
            (RecognitionState.Listening, RecognitionTrigger.Stop) => true,
            (RecognitionState.Muted, RecognitionTrigger.Unmute) => true,
            (RecognitionState.Muted, RecognitionTrigger.Stop) => true,
            (RecognitionState.Finalizing, RecognitionTrigger.FlushDone) => true,
            (_, RecognitionTrigger.Reset) => true,
            _ => false
        };

    public RecognitionState Fire(RecognitionTrigger trigger)
    {
        if (!CanFire(trigger))
            throw new InvalidOperationException(
                $"Invalid state transition: {CurrentState} -> {trigger}");

        CurrentState = (CurrentState, trigger) switch
        {
            (RecognitionState.Idle, RecognitionTrigger.Start) => RecognitionState.Initializing,
            (RecognitionState.Error, RecognitionTrigger.Start) => RecognitionState.Initializing,
            (RecognitionState.Listening, RecognitionTrigger.Start) => RecognitionState.Listening,
            (RecognitionState.Initializing, RecognitionTrigger.InitOk) => RecognitionState.Listening,
            (RecognitionState.Initializing, RecognitionTrigger.InitFail) => RecognitionState.Error,
            (RecognitionState.Listening, RecognitionTrigger.InitOk) => RecognitionState.Listening,
            (RecognitionState.Listening, RecognitionTrigger.InitFail) => RecognitionState.Error,
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

    public bool IsActive => CurrentState is RecognitionState.Listening or RecognitionState.Muted;
}
