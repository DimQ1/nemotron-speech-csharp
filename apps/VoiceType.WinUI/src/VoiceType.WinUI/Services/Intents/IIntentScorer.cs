namespace VoiceType.WinUI.Services.Intents;

public sealed record VoiceIntentScores(
    IReadOnlyDictionary<string, float> Scores,
    string TopIntent,
    float TopScore);

public interface IIntentScorer : IDisposable
{
    string ModelId { get; }
    Task<VoiceIntentScores> ScoreAsync(string text, CancellationToken ct);
}