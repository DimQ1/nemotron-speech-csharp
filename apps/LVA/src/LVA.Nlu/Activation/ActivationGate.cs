using LVA.Core.Configuration;
using LVA.Core.Intents;

namespace LVA.Nlu.Activation;

/// <summary>
/// Two-signal activation gate (TZ §4.5): addressing counts only when
/// (1) semantic L1 score ≥ threshold AND (2) a lexical trigger matches.
/// Protects against ASR errors and semantically-similar background speech.
/// </summary>
public sealed class ActivationGate
{
    private readonly IntentTaxonomy _taxonomy;
    private readonly float _threshold;

    public ActivationGate(IntentTaxonomy taxonomy, IntentEngineOptions options)
    {
        _taxonomy = taxonomy;
        _threshold = options.L1.ThresholdAddressing;
    }

    /// <summary>
    /// Returns true when both signals agree on the given text.
    /// </summary>
    public bool IsActivated(string text, float semanticAddressingScore)
    {
        if (semanticAddressingScore < _threshold)
            return false;
        return HasLexicalMatch(text);
    }

    /// <summary>Normalized substring match of addressing trigger words.</summary>
    public bool HasLexicalMatch(string text)
    {
        var addressing = _taxonomy.Find(IntentIds.Addressing);
        if (addressing?.LexicalTriggers is null)
            return false;

        var lower = text.ToLowerInvariant();
        return addressing.LexicalTriggers.Any(t =>
            lower.Contains(t.ToLowerInvariant(), StringComparison.Ordinal));
    }
}
