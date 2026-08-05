namespace VoiceType.Services.Intents;

/// <summary>
/// L0 heuristic scorer (always built-in): lexical triggers from the taxonomy,
/// RU/EN command keywords. Synchronous, sub-millisecond.
/// </summary>
public static class HeuristicScorer
{
    public static VoiceIntentScores Score(string text, VoiceIntentTaxonomy taxonomy)
    {
        var lower = text.ToLowerInvariant();
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            [VoiceIntentIds.Dictation] = 0f,
            [VoiceIntentIds.StartTyping] = 0f,
            [VoiceIntentIds.StopTyping] = 0f,
            [VoiceIntentIds.SwitchTarget] = 0f,
            [VoiceIntentIds.Cancel] = 0f,
        };

        foreach (var intent in taxonomy.Intents)
        {
            if (intent.LexicalTriggers is null) continue;
            foreach (var trigger in intent.LexicalTriggers)
            {
                if (lower.Contains(trigger.ToLowerInvariant(), StringComparison.Ordinal))
                    scores[intent.Id] = Math.Max(scores.GetValueOrDefault(intent.Id), 0.9f);
            }
        }

        var top = scores.OrderByDescending(kv => kv.Value).First();
        // No trigger fired → treat as plain dictation.
        if (top.Value <= 0f)
            return new VoiceIntentScores(scores, VoiceIntentIds.Dictation, 1f);
        return new VoiceIntentScores(scores, top.Key, top.Value);
    }
}
