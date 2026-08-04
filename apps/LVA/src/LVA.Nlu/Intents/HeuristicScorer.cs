using LVA.Core.Intents;

namespace LVA.Nlu.Intents;

/// <summary>
/// L0 heuristic scorer (always built-in, TZ §4.3): question words, "?",
/// lexical triggers from the taxonomy. Runs synchronously in &lt;1 ms.
/// </summary>
public static class HeuristicScorer
{
    private static readonly string[] QuestionWordsRu =
        { "как", "почему", "зачем", "сколько", "когда", "где", "что", "какой", "какая", "какие", "кто", "чей" };
    private static readonly string[] QuestionWordsEn =
        { "how", "why", "what", "when", "where", "who", "which", "whose" };

    public static IntentScores Score(string text, IntentTaxonomy taxonomy)
    {
        var lower = text.ToLowerInvariant();
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            [IntentIds.Addressing] = 0f,
            [IntentIds.Question] = 0f,
            [IntentIds.Execute] = 0f,
            [IntentIds.Cancel] = 0f,
            [IntentIds.Background] = 0f
        };

        // Lexical triggers from taxonomy (addressing / execute / cancel).
        foreach (var intent in taxonomy.Intents)
        {
            if (intent.LexicalTriggers is null) continue;
            foreach (var trigger in intent.LexicalTriggers)
            {
                if (lower.Contains(trigger.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    scores[intent.Id] = Math.Max(scores.GetValueOrDefault(intent.Id), 0.7f);
                }
            }
        }

        // Question words at word boundaries.
        var trimChars = new[] { '?', ',', '!', '.' };
        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 0)
        {
            var first = words[0].TrimEnd(trimChars);
            if (QuestionWordsRu.Contains(first) || QuestionWordsEn.Contains(first))
                scores[IntentIds.Question] = Math.Max(scores[IntentIds.Question], 0.65f);
            if (words.Any(w => QuestionWordsRu.Contains(w.TrimEnd(trimChars)) || QuestionWordsEn.Contains(w.TrimEnd(trimChars))))
                scores[IntentIds.Question] = Math.Max(scores[IntentIds.Question], 0.5f);
        }

        // Trailing question mark is a strong question signal.
        if (text.TrimEnd().EndsWith('?'))
            scores[IntentIds.Question] = Math.Max(scores[IntentIds.Question], 0.8f);

        var top = scores.OrderByDescending(kv => kv.Value).First();
        return new IntentScores(scores, top.Key, top.Value);
    }
}
