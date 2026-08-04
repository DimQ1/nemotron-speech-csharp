using LVA.Core.Events;

namespace LVA.Core.StateMachine;

/// <summary>Dialog states per TZ §4.7.</summary>
public enum ConversationState
{
    Idle,
    ContextCapture,
    QuestionReady
}

/// <summary>
/// Finite-state machine for the dialog lifecycle.
/// Publishes every transition as an event for UI and tests.
/// </summary>
public sealed class ConversationStateMachine
{
    private readonly TimeSpan _captureTimeout;

    public ConversationStateMachine(TimeSpan? captureTimeout = null)
    {
        _captureTimeout = captureTimeout ?? TimeSpan.FromSeconds(90);
    }

    public ConversationState State { get; private set; } = ConversationState.Idle;
    public DateTimeOffset CaptureStartedAt { get; private set; }

    /// <summary>Raised on every state transition (old state, new state, reason).</summary>
    public event Action<ConversationState, ConversationState, string>? Transitioned;

    /// <summary>Speech activity while idle keeps ASR/L1 warm; no transition needed.</summary>
    public void OnSpeechStarted() { /* stays Idle until activation */ }

    /// <summary>ActivationGate accepted addressing: Idle → ContextCapture.</summary>
    public bool OnActivation()
    {
        if (State == ConversationState.ContextCapture)
            return false; // already capturing; re-addressing is ignored
        Transition(ConversationState.ContextCapture, "addressing");
        CaptureStartedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>Question assembled: ContextCapture → QuestionReady.</summary>
    public bool OnQuestionReady()
    {
        if (State != ConversationState.ContextCapture)
            return false;
        Transition(ConversationState.QuestionReady, "question-ready");
        return true;
    }

    /// <summary>Orchestrator picked up the question: back to Idle.</summary>
    public void OnDispatched() => Transition(ConversationState.Idle, "dispatched");

    /// <summary>Cancel intent or timeout: buffer cleared, back to Idle.</summary>
    public void Reset(string reason) => Transition(ConversationState.Idle, reason);

    /// <summary>True when the capture window has expired and the dialog must reset.</summary>
    public bool IsTimedOut(DateTimeOffset now)
        => State == ConversationState.ContextCapture && now - CaptureStartedAt > _captureTimeout;

    private void Transition(ConversationState next, string reason)
    {
        if (State == next)
            return;
        var prev = State;
        State = next;
        Transitioned?.Invoke(prev, next, reason);
    }
}
