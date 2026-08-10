namespace VoiceType.WinUI.Services.Intents;

public static class HeuristicScorer
{
    public static VoiceIntentScores Score(string text, VoiceIntentTaxonomy taxonomy)
    {
        var lower = NormalizeForMatching(text);
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
            if (intent.LexicalTriggers is null)
                continue;

            foreach (var trigger in intent.LexicalTriggers)
            {
                var score = GetTriggerScore(lower, trigger);
                if (score > 0)
                    scores[intent.Id] = Math.Max(scores.GetValueOrDefault(intent.Id), score);
            }
        }

        if (ContainsRussianTargetCommand(lower))
            scores[VoiceIntentIds.SwitchTarget] = Math.Max(
                scores.GetValueOrDefault(VoiceIntentIds.SwitchTarget),
                0.9f);

        var top = scores.OrderByDescending(pair => pair.Value).First();
        return top.Value > 0f
            ? new VoiceIntentScores(scores, top.Key, top.Value)
            : new VoiceIntentScores(scores, VoiceIntentIds.Dictation, 1f);
    }

    public static string NormalizeForMatching(string text) => text
        .ToLowerInvariant()
        .Replace("ночни", "начни", StringComparison.Ordinal)
        .Replace("на печатай", "напечатай", StringComparison.Ordinal)
        .Replace("на печатать", "напечатать", StringComparison.Ordinal);

    private static bool ContainsRussianTargetCommand(string text)
    {
        foreach (var verb in new[]
                 {
                     "напечатай", "напечатать",
                     "набери", "набрать",
                     "введи", "ввести",
                     "печатай", "печатать",
                 })
        {
            var verbIndex = text.IndexOf(verb, StringComparison.Ordinal);
            if (verbIndex < 0)
                continue;

            if (text.IndexOf(" в ", verbIndex + verb.Length, StringComparison.Ordinal) >= 0)
                return true;
        }

        return false;
    }

    private static float GetTriggerScore(string text, string trigger)
    {
        var normalizedTrigger = NormalizeForMatching(trigger);
        var exactIndex = text.LastIndexOf(normalizedTrigger, StringComparison.Ordinal);
        if (exactIndex >= 0)
        {
            // Streaming ASR sends an accumulated sentence. A later command must
            // override an earlier one rather than the dictionary insertion order.
            return 0.9f + (float)exactIndex / Math.Max(text.Length, 1) * 0.09f;
        }

        var textWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var triggerWords = normalizedTrigger.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (triggerWords.Length == 0)
            return 0f;

        var matches = triggerWords.All(triggerWord => textWords.Any(textWord =>
            textWord.StartsWith(triggerWord, StringComparison.Ordinal)
            || triggerWord.StartsWith(textWord, StringComparison.Ordinal)
            || LevenshteinDistance(textWord, triggerWord) <= Math.Max(1, triggerWord.Length / 5)));
        return matches ? 0.85f : 0f;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1));
            }
            previous = current;
        }

        return previous[right.Length];
    }
}