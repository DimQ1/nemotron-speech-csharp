namespace SpeechLib.Translation;

/// <summary>
/// Defences against the ways a small instruction-tuned model derails in live
/// translation: echoing the context it was given, "translating" text that is already
/// in the target language, and producing output far longer than its input.
/// </summary>
public static class TranslationGuards
{
    /// <summary>
    /// True when <paramref name="text"/> is evidently written in <paramref name="targetLanguage"/>
    /// already, judged by script. Only scripts that identify a language (or a small
    /// family) are used: Cyrillic without Ukrainian letters → Russian, kana → Japanese,
    /// Hangul → Korean, Han → Chinese, Arabic, Devanagari. Latin-script languages cannot
    /// be told apart this way and always return false.
    /// </summary>
    /// <remarks>
    /// Asking Gemma to translate Russian into Russian (recognition language "auto",
    /// target Russian) makes it echo and rewrite its input; with previous-sentence
    /// context the echo accumulated sentence after sentence. Such text is passed through.
    /// </remarks>
    public static bool IsAlreadyInTarget(string text, string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(targetLanguage))
            return false;

        int letters = 0, cyrillic = 0, ukrainian = 0, latin = 0, han = 0, kana = 0, hangul = 0, arabic = 0, devanagari = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
                continue;
            letters++;
            switch (c)
            {
                case >= 'Ѐ' and <= 'ӿ':
                    cyrillic++;
                    if (c is 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є' or 'Ґ')
                        ukrainian++;
                    break;
                case >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ':
                    latin++;
                    break;
                case >= '぀' and <= 'ヿ':
                    kana++;
                    break;
                case >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ':
                    hangul++;
                    break;
                case >= '一' and <= '鿿':
                    han++;
                    break;
                case >= '؀' and <= 'ۿ':
                    arabic++;
                    break;
                case >= 'ऀ' and <= 'ॿ':
                    devanagari++;
                    break;
            }
        }

        if (letters < 2)
            return false;

        bool Mostly(int count) => count >= letters * 0.8;

        return targetLanguage.Trim().ToLowerInvariant() switch
        {
            "russian" => Mostly(cyrillic) && ukrainian == 0,
            "ukrainian" => Mostly(cyrillic) && ukrainian > 0,
            "japanese" => kana > 0 && Mostly(kana + han),
            "korean" => Mostly(hangul),
            "chinese" => Mostly(han) && kana == 0,
            "arabic" => Mostly(arabic),
            "hindi" => Mostly(devanagari),
            _ => false,
        };
    }

    /// <summary>
    /// Removes a leading echo of the context (the previous sentence or its translation)
    /// from <paramref name="output"/>. While streaming (<paramref name="final"/> false) an
    /// output that is still only the beginning of the echo yields "" so the echo never
    /// flashes on screen. A final output that is nothing but the echo is kept: the
    /// speaker may really have said the same sentence twice.
    /// </summary>
    public static string StripContextEcho(string output, TranslationRequest request, bool final)
    {
        if (string.IsNullOrEmpty(output) || !request.HasContext)
            return output;

        foreach (var echo in new[] { request.PreviousTranslation!, request.PreviousSource! })
        {
            var match = LooseMatch(output, echo);
            if (match.Matched)
            {
                // Skip what closes the echoed sentence (punctuation, closing brackets and
                // quotes, whitespace) but not an opening bracket or quote of the answer.
                var start = match.Consumed;
                while (start < output.Length
                       && (char.IsWhiteSpace(output[start])
                           || output[start] is '.' or ',' or ';' or ':' or '!' or '?' or '…' or '-' or '—' or ')' or ']' or '}' or '»' or '”'))
                    start++;
                var rest = output[start..];
                if (rest.Length > 0 || !final)
                    return rest;
                return output;
            }

            if (match.OutputExhausted && !final)
                return "";
        }

        return output;
    }

    /// <summary>True when a translation is implausibly long for its source (a decode that ran away).</summary>
    public static bool IsRunaway(string output, string source) =>
        output.Length > source.Length * 3 + 40;

    private readonly record struct Match(bool Matched, int Consumed, bool OutputExhausted);

    /// <summary>
    /// Compares letters and digits only, case-insensitively: does <paramref name="output"/>
    /// start with <paramref name="prefix"/>?
    /// </summary>
    private static Match LooseMatch(string output, string prefix)
    {
        int i = 0, j = 0, compared = 0;
        while (true)
        {
            while (j < prefix.Length && !char.IsLetterOrDigit(prefix[j]))
                j++;
            if (j >= prefix.Length)
                return new Match(compared > 0, i, false);

            while (i < output.Length && !char.IsLetterOrDigit(output[i]))
                i++;
            if (i >= output.Length)
                return new Match(false, i, compared > 0);

            if (char.ToLowerInvariant(output[i]) != char.ToLowerInvariant(prefix[j]))
                return new Match(false, 0, false);

            i++;
            j++;
            compared++;
        }
    }
}
