using LVA.Core.Configuration;
using LVA.Core.Intents;

namespace LVA.Nlu.Intents;

/// <summary>
/// Intent cascade (TZ §4.3): L0 heuristics → L1 embedding scoring → hysteresis.
/// Knows nothing about concrete models — they come through IIntentModel.
/// </summary>
public sealed class IntentEngine
{
    private readonly IIntentModel? _l1;
    private readonly IntentTaxonomy _taxonomy;
    private readonly IntentEngineOptions _options;
    private readonly HysteresisFilter _addressingHysteresis;

    public IntentEngine(IntentEngineOptions options, IntentTaxonomy taxonomy, IIntentModel? l1)
    {
        _options = options;
        _taxonomy = taxonomy;
        _l1 = l1;
        _addressingHysteresis = new HysteresisFilter(options.HysteresisM, options.HysteresisN, options.L1.ThresholdAddressing);
    }

    /// <summary>
    /// Scores a (stabilized) partial hypothesis. L0 runs always;
    /// L1 runs only when its score can change the outcome (cheap gate).
    /// </summary>
    public async Task<IntentScores> ScoreAsync(string text, CancellationToken ct)
    {
        var l0 = HeuristicScorer.Score(text, _taxonomy);

        if (_l1 is null || string.IsNullOrWhiteSpace(text))
            return l0;

        var l1 = await _l1.ScoreAsync(new IntentInput(text), ct);

        // Fuse: max of L0 and L1 per intent (L1 is semantic, L0 is lexical).
        var fused = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in l0.Scores.Keys.Union(l1.Scores.Keys))
            fused[key] = Math.Max(l0.Scores.GetValueOrDefault(key), l1.Scores.GetValueOrDefault(key));

        var top = fused.OrderByDescending(kv => kv.Value).First();
        return new IntentScores(fused, top.Key, top.Value);
    }

    /// <summary>Feeds an addressing score into the hysteresis filter.</summary>
    public bool UpdateAddressingHysteresis(float addressingScore)
        => _addressingHysteresis.Update(addressingScore);

    public bool AddressingActive => _addressingHysteresis.IsActive;

    public void ResetHysteresis() => _addressingHysteresis.Reset();
}
