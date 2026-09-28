using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpeechLib.Translation;

/// <summary>
/// Normalises what an instruction-tuned model returns for "reply with only the
/// translation": drops label prefixes ("Translation:", "Перевод:"), wrapping quotes and
/// code fences, a <c>{"translation": ...}</c> envelope, special tokens, and cuts
/// degenerate repetition loops so a runaway decode never floods the display.
/// </summary>
public static partial class TranslationOutputCleaner
{
    /// <summary>A phrase of this many words repeated back-to-back this often is a decode loop.</summary>
    private const int LoopWords = 3;
    private const int LoopRepeats = 3;

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var value = text.Trim();
        value = StripCodeFence(value);
        value = UnwrapJsonEnvelope(value);
        value = LabelPrefix().Replace(value, "");
        value = SpecialTokens().Replace(value, "");
        value = StripWrappingQuotes(value);
        value = CutRepetitionLoop(value);
        return Whitespace().Replace(value, " ").Trim();
    }

    private static string StripCodeFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
            return value;

        var firstBreak = value.IndexOf('\n');
        if (firstBreak < 0)
            return value;

        var body = value[(firstBreak + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }

    private static string UnwrapJsonEnvelope(string value)
    {
        if (!value.StartsWith('{') || !value.EndsWith('}'))
            return value;

        try
        {
            using var document = JsonDocument.Parse(value);
            foreach (var name in new[] { "translation", "text", "output" })
            {
                if (document.RootElement.TryGetProperty(name, out var property)
                    && property.ValueKind == JsonValueKind.String)
                    return property.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Braces inside a real translation; keep it.
        }

        return value;
    }

    private static string StripWrappingQuotes(string value)
    {
        value = value.Trim();
        if (value.Length < 2)
            return value;

        var first = value[0];
        var last = value[^1];
        var quoted = (first, last) is ('"', '"') or ('“', '”') or ('«', '»') or ('\'', '\'') or ('`', '`');
        if (!quoted)
            return value;

        // Only unwrap when the quotes enclose the whole text, not a quoted fragment.
        var inner = value[1..^1];
        return inner.Contains(first) ? value : inner.Trim();
    }

    /// <summary>
    /// Truncates after the first occurrence of a phrase that repeats itself back-to-back
    /// <see cref="LoopRepeats"/> times, keeping one copy: "yes yes yes yes" → "yes".
    /// </summary>
    private static string CutRepetitionLoop(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < LoopWords * LoopRepeats)
            return value;

        for (var start = 0; start < words.Length; start++)
        {
            for (var size = 1; size <= LoopWords && start + size * LoopRepeats <= words.Length; size++)
            {
                if (!RepeatsAt(words, start, size, LoopRepeats))
                    continue;

                var kept = new StringBuilder();
                for (var i = 0; i < start + size; i++)
                    kept.Append(i > 0 ? " " : "").Append(words[i]);
                return kept.ToString();
            }
        }

        return value;
    }

    private static bool RepeatsAt(string[] words, int start, int size, int repeats)
    {
        for (var copy = 1; copy < repeats; copy++)
        {
            for (var i = 0; i < size; i++)
            {
                if (!string.Equals(words[start + i], words[start + copy * size + i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }

    [GeneratedRegex(@"^\s*(?:translation|translated text|перевод|переклад|übersetzung|traduction|traducción)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LabelPrefix();

    [GeneratedRegex(@"<\|?(?:unk|pad|endoftext|eos|bos|start_of_turn|end_of_turn|nospeech)\|?>")]
    private static partial Regex SpecialTokens();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Whitespace();
}
