using VoiceType.Models;

namespace VoiceType.Services.Intents;

/// <summary>Result of routing one recognized utterance through the intent cascade.</summary>
public sealed record VoiceIntentResult(
    string Intent,
    float Score,
    bool IsCommand)
{
    /// <summary>Plain dictation — text should be injected as-is.</summary>
    public static VoiceIntentResult Dictation { get; } =
        new(VoiceIntentIds.Dictation, 1f, IsCommand: false);
}

/// <summary>
/// Voice-intent cascade facade (ported from LVA IntentEngine, TZ §4.3):
/// L0 heuristics run always; L1 semantic scorer runs only when it can change
/// the outcome; per-intent hysteresis prevents flicker on partial hypotheses.
///
/// The router owns no UI and no Win32 state — it only classifies text.
/// When Voice Intent Mode is disabled the router is never constructed.
/// </summary>
public sealed class VoiceIntentRouter : IDisposable
{
    private readonly VoiceIntentTaxonomy _taxonomy;
    private readonly IIntentScorer? _l1;
    private readonly float _threshold;
    private readonly int _m, _n;
    private readonly Dictionary<string, HysteresisFilter> _hysteresis = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public VoiceIntentRouter(VoiceIntentSettings settings, VoiceIntentTaxonomy? taxonomy = null, IIntentScorer? l1 = null)
    {
        _taxonomy = taxonomy ?? VoiceIntentTaxonomy.Load(settings.CustomIntentsFile);
        _l1 = l1;
        _threshold = settings.ConfidenceThreshold;
        _m = settings.HysteresisM;
        _n = settings.HysteresisN;
        foreach (var intent in _taxonomy.Intents)
            _hysteresis[intent.Id] = new HysteresisFilter(_m, _n, _threshold);
    }

    /// <summary>True when a semantic L1 scorer is attached (model loaded).</summary>
    public bool HasSemanticScorer => _l1 is not null;

    /// <summary>
    /// Classifies one recognized utterance. Returns
    /// <see cref="VoiceIntentResult.Dictation"/> when no command wins.
    /// </summary>
    public async Task<VoiceIntentResult> RouteAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return VoiceIntentResult.Dictation;

        var l0 = HeuristicScorer.Score(text, _taxonomy);

        // A confident L0 lexical trigger fires immediately (commands are discrete
        // phrases, not a continuously-addressed state like LVA's "addressing").
        if (l0.TopIntent != VoiceIntentIds.Dictation && l0.TopScore >= _threshold)
            return new VoiceIntentResult(l0.TopIntent, l0.TopScore, IsCommand: true);

        var fused = new Dictionary<string, float>(l0.Scores, StringComparer.OrdinalIgnoreCase);

        // Cheap gate: L1 runs only when it could change the outcome —
        // i.e. L0 found no confident trigger yet.
        if (_l1 is not null)
        {
            var l1 = await _l1.ScoreAsync(text, ct).ConfigureAwait(false);
            foreach (var (key, value) in l1.Scores)
                fused[key] = Math.Max(fused.GetValueOrDefault(key), value);
        }

        // Semantic (L1) detections go through per-intent hysteresis to avoid
        // acting on a single noisy embedding match on partial hypotheses.
        string? winner = null;
        float winnerScore = 0f;
        foreach (var (id, score) in fused)
        {
            if (id == VoiceIntentIds.Dictation) continue;
            if (!_hysteresis.TryGetValue(id, out var filter)) continue;
            if (filter.Update(score) && score >= winnerScore)
            {
                winner = id;
                winnerScore = score;
            }
        }

        if (winner is null)
            return VoiceIntentResult.Dictation;

        return new VoiceIntentResult(winner, winnerScore, IsCommand: true);
    }

    /// <summary>Clears hysteresis windows (e.g. on recording stop / mode toggle).</summary>
    public void Reset()
    {
        foreach (var filter in _hysteresis.Values)
            filter.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _l1?.Dispose();
    }
}
