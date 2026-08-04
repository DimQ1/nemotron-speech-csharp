namespace LVA.Nlu.Intents;

/// <summary>Input for intent scoring.</summary>
public sealed record IntentInput(string Text, string? StablePrefix = null);

/// <summary>Per-intent cosine scores plus the winning intent.</summary>
public sealed record IntentScores(
    IReadOnlyDictionary<string, float> Scores,
    string TopIntent,
    float TopScore);

/// <summary>Any pluggable ML intent model (TZ §4.3). Selected via configuration.</summary>
public interface IIntentModel : IDisposable
{
    string ModelId { get; }
    Task<IntentScores> ScoreAsync(IntentInput input, CancellationToken ct);
}
