using LVA.Core.Configuration;
using LVA.Core.Context;
using LVA.Core.Events;
using LVA.Core.Intents;
using LVA.Core.StateMachine;
using LVA.Nlu.Activation;
using LVA.Nlu.Intents;
using LVA.Nlu.Stabilization;

namespace LVA.Nlu.Pipeline;

/// <summary>
/// NLU conversation pipeline (TZ §2.2, §4): VAD events → ASR transcripts →
/// stabilizer → IntentEngine (L0/L1) → ActivationGate → ContextBuffer →
/// completion detection → QuestionReady. Platform-agnostic; audio capture and
/// ASR feed it through the On* methods. Emits events for UI/orchestrator.
/// </summary>
public sealed class ConversationPipeline
{
    private readonly ConversationStateMachine _stateMachine;
    private readonly ContextBuffer _buffer;
    private readonly IntentEngine _intentEngine;
    private readonly ActivationGate _activationGate;
    private readonly HypothesisStabilizer _stabilizer;
    private readonly IntentTaxonomy _taxonomy;
    private readonly VadOptions _vadOptions;

    private DateTimeOffset _lastSpeechActivity;
    private bool _lastSegmentWasQuestion;

    public ConversationPipeline(
        LvaOptions options,
        IntentTaxonomy taxonomy,
        IntentEngine intentEngine,
        ConversationStateMachine? stateMachine = null,
        ContextBuffer? buffer = null)
    {
        _taxonomy = taxonomy;
        _intentEngine = intentEngine;
        _vadOptions = options.Vad;
        _stateMachine = stateMachine ?? new ConversationStateMachine();
        _buffer = buffer ?? new ContextBuffer();
        _activationGate = new ActivationGate(taxonomy, options.IntentEngine);
        _stabilizer = new HypothesisStabilizer();
    }

    public event Action<ActivationRequestedEvent>? ActivationRequested;
    public event Action<QuestionReadyEvent>? QuestionReady;
    public event Action<ConversationResetEvent>? Reset;
    public event Action<string, IntentScores>? IntentScored;   // diagnostics/UI

    public ConversationState State => _stateMachine.State;

    /// <summary>VAD: speech started.</summary>
    public void OnSpeechStarted()
    {
        _lastSpeechActivity = DateTimeOffset.UtcNow;
        _stateMachine.OnSpeechStarted();
    }

    /// <summary>ASR partial hypothesis.</summary>
    public async Task OnPartialTranscriptAsync(string text, CancellationToken ct = default)
    {
        _lastSpeechActivity = DateTimeOffset.UtcNow;
        var stabilized = _stabilizer.Update(text);
        var scores = await _intentEngine.ScoreAsync(stabilized, ct);
        IntentScored?.Invoke(stabilized, scores);

        if (_stateMachine.State == ConversationState.Idle &&
            scores.Scores.TryGetValue(IntentIds.Addressing, out var addrScore) &&
            _activationGate.IsActivated(stabilized, addrScore))
        {
            if (_stateMachine.OnActivation())
            {
                _buffer.TryAdd(new ContextSegment(stabilized, "addressing",
                    DateTimeOffset.UtcNow, addrScore));
                ActivationRequested?.Invoke(new ActivationRequestedEvent(
                    stabilized, addrScore, DateTimeOffset.UtcNow));
            }
        }

        if (scores.Scores.GetValueOrDefault(IntentIds.Cancel) >= 0.7f &&
            _stateMachine.State == ConversationState.ContextCapture)
        {
            CancelConversation("cancel-intent");
        }
    }

    /// <summary>ASR final transcript for a VAD segment.</summary>
    public async Task OnFinalTranscriptAsync(string text, CancellationToken ct = default)
    {
        _lastSpeechActivity = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var scores = await _intentEngine.ScoreAsync(text, ct);

        if (_stateMachine.State == ConversationState.Idle &&
            scores.Scores.TryGetValue(IntentIds.Addressing, out var addrScore) &&
            _activationGate.IsActivated(text, addrScore))
        {
            if (_stateMachine.OnActivation())
            {
                _buffer.TryAdd(new ContextSegment(text, "addressing", DateTimeOffset.UtcNow, addrScore));
                ActivationRequested?.Invoke(new ActivationRequestedEvent(text, addrScore, DateTimeOffset.UtcNow));
            }
            _stabilizer.Reset();
            return;
        }

        if (_stateMachine.State != ConversationState.ContextCapture)
            return;

        var topIntent = scores.TopIntent;
        switch (topIntent)
        {
            case IntentIds.Cancel when scores.TopScore >= 0.7f:
                CancelConversation("cancel-intent");
                return;

            case IntentIds.Execute when scores.TopScore >= 0.6f:
                EmitQuestionReady("execute", scores.TopScore);
                return;

            case IntentIds.Question:
                _lastSegmentWasQuestion = true;
                _buffer.TryAdd(new ContextSegment(text, "question_part",
                    DateTimeOffset.UtcNow, scores.TopScore));
                break;

            case IntentIds.Background:
                // never stored (TZ §7 privacy)
                break;

            default:
                if (_stateMachine.State == ConversationState.ContextCapture)
                    _buffer.TryAdd(new ContextSegment(text, "question_part",
                        DateTimeOffset.UtcNow, scores.TopScore));
                break;
        }
        _stabilizer.Reset();
    }

    /// <summary>
    /// VAD: speech ended. Completion signals (TZ §4.6): silence > T_end after
    /// a question segment, or "?" + shorter pause — evaluated by the caller
    /// cadence via <see cref="Tick"/>.
    /// </summary>
    public void OnSpeechEnded(TimeSpan segmentDuration)
    {
        _lastSpeechActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Periodic tick (called by the host every ~250 ms) to evaluate
    /// silence-based completion and capture timeout.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        if (_stateMachine.State != ConversationState.ContextCapture)
            return;

        if (_stateMachine.IsTimedOut(now))
        {
            CancelConversation("timeout");
            return;
        }

        var silence = now - _lastSpeechActivity;
        if (_lastSegmentWasQuestion && silence.TotalMilliseconds > _vadOptions.EndOfQuestionSilenceMs)
            EmitQuestionReady("silence-timeout", 1.0);
    }

    private void EmitQuestionReady(string signal, double confidence)
    {
        if (!_stateMachine.OnQuestionReady())
            return;

        var context = _buffer.AssembleContext();
        QuestionReady?.Invoke(new QuestionReadyEvent(
            context, "question", new Dictionary<string, string>(), confidence, DateTimeOffset.UtcNow));

        _buffer.Clear();
        _lastSegmentWasQuestion = false;
        _intentEngine.ResetHysteresis();
        _stateMachine.OnDispatched();
    }

    private void CancelConversation(string reason)
    {
        _buffer.Clear();
        _lastSegmentWasQuestion = false;
        _intentEngine.ResetHysteresis();
        _stabilizer.Reset();
        _stateMachine.Reset(reason);
        Reset?.Invoke(new ConversationResetEvent(reason, DateTimeOffset.UtcNow));
    }
}
