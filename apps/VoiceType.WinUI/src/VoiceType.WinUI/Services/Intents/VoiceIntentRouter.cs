using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services.Intents;

public sealed record VoiceIntentResult(string Intent, float Score, bool IsCommand)
{
    public static VoiceIntentResult Dictation { get; } =
        new(VoiceIntentIds.Dictation, 1f, IsCommand: false);
}

public sealed class VoiceIntentRouter : IDisposable
{
    private readonly VoiceIntentTaxonomy _taxonomy;
    private readonly IIntentScorer? _semanticScorer;
    private readonly float _threshold;
    private readonly Dictionary<string, HysteresisFilter> _hysteresis = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public VoiceIntentRouter(
        VoiceIntentSettings settings,
        VoiceIntentTaxonomy? taxonomy = null,
        IIntentScorer? semanticScorer = null)
    {
        _taxonomy = taxonomy ?? VoiceIntentTaxonomy.Load(settings.CustomIntentsFile);
        _semanticScorer = semanticScorer;
        _threshold = settings.ConfidenceThreshold;

        foreach (var intent in _taxonomy.Intents)
        {
            _hysteresis[intent.Id] = new HysteresisFilter(
                settings.HysteresisM,
                settings.HysteresisN,
                _threshold);
        }
    }

    public bool HasSemanticScorer => _semanticScorer is not null;

    public VoiceIntentResult RouteLexical(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return VoiceIntentResult.Dictation;

        var lexical = HeuristicScorer.Score(text, _taxonomy);
        return lexical.TopIntent != VoiceIntentIds.Dictation && lexical.TopScore >= _threshold
            ? new VoiceIntentResult(lexical.TopIntent, lexical.TopScore, IsCommand: true)
            : VoiceIntentResult.Dictation;
    }

    public async Task<VoiceIntentResult> RouteAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return VoiceIntentResult.Dictation;

        var lexicalResult = RouteLexical(text);
        if (lexicalResult.IsCommand)
            return lexicalResult;

        var lexical = HeuristicScorer.Score(text, _taxonomy);

        var fused = new Dictionary<string, float>(lexical.Scores, StringComparer.OrdinalIgnoreCase);
        if (_semanticScorer is not null)
        {
            var semantic = await _semanticScorer.ScoreAsync(text, ct).ConfigureAwait(false);
            foreach (var pair in semantic.Scores)
                fused[pair.Key] = Math.Max(fused.GetValueOrDefault(pair.Key), pair.Value);
        }

        string? winner = null;
        var winnerScore = 0f;
        foreach (var pair in fused)
        {
            if (pair.Key == VoiceIntentIds.Dictation)
                continue;

            if (!_hysteresis.TryGetValue(pair.Key, out var filter))
                continue;

            if (filter.Update(pair.Value) && pair.Value >= winnerScore)
            {
                winner = pair.Key;
                winnerScore = pair.Value;
            }
        }

        return winner is null
            ? VoiceIntentResult.Dictation
            : new VoiceIntentResult(winner, winnerScore, IsCommand: true);
    }

    public void Reset()
    {
        foreach (var filter in _hysteresis.Values)
            filter.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _semanticScorer?.Dispose();
    }
}