namespace LVA.Core.Events;

/// <summary>VAD detected the start of a speech segment.</summary>
public sealed record SpeechStartedEvent(DateTimeOffset Timestamp);

/// <summary>VAD detected the end of a speech segment.</summary>
public sealed record SpeechEndedEvent(DateTimeOffset Timestamp, TimeSpan Duration);

/// <summary>Partial (unstable) ASR hypothesis update.</summary>
public sealed record PartialTranscriptEvent(string Text, DateTimeOffset Timestamp);

/// <summary>Final ASR transcript for a VAD segment.</summary>
public sealed record FinalTranscriptEvent(string Text, TimeSpan Duration, DateTimeOffset Timestamp);

/// <summary>ActivationGate accepted an addressing intent (semantic + lexical).</summary>
public sealed record ActivationRequestedEvent(string TriggerText, double SemanticScore, DateTimeOffset Timestamp);

/// <summary>A fully-assembled user question is ready for orchestration.</summary>
public sealed record QuestionReadyEvent(
    string Context,
    string Intent,
    IReadOnlyDictionary<string, string> Slots,
    double Confidence,
    DateTimeOffset Timestamp);

/// <summary>Conversation returned to idle (cancel, timeout, or completion).</summary>
public sealed record ConversationResetEvent(string Reason, DateTimeOffset Timestamp);
