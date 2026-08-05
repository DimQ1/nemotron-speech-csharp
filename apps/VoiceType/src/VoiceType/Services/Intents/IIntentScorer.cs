namespace VoiceType.Services.Intents;

/// <summary>Per-intent scores plus the winning intent and a confidence value.</summary>
public sealed record VoiceIntentScores(
    IReadOnlyDictionary<string, float> Scores,
    string TopIntent,
    float TopScore);

/// <summary>Pluggable semantic scorer (e.g. MiniLM ONNX). L0 heuristics are separate.</summary>
public interface IIntentScorer : IDisposable
{
    string ModelId { get; }
    Task<VoiceIntentScores> ScoreAsync(string text, CancellationToken ct);
}
