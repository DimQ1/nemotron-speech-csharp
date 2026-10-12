using System.Globalization;
using System.Text;

namespace VoiceType.Uno.History;

/// <summary>
/// Search/diff normalisation: case-fold, strip diacritics (é→e, ё→е) while keeping
/// letters whose identity depends on the mark in Russian (й stays й).
/// </summary>
public static class TextNormalizer
{
    private const char CombiningBreve = '̆';

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                // и + breve = й: a different letter, not an accent.
                if (c == CombiningBreve && sb.Length > 0 && sb[^1] == 'и')
                    sb[^1] = 'й';
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Normalised comparison key of one word: <see cref="Normalize"/> without
    /// punctuation/symbols. Empty for punctuation-only tokens ("—", "...").
    /// </summary>
    public static string NormalizeToken(string word)
    {
        var normalized = Normalize(word);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Normalised words of <paramref name="text"/> (punctuation-only tokens dropped).</summary>
    public static IReadOnlyList<string> NormalizeWords(string? text) =>
        TextTokens.SplitWords(text).Select(NormalizeToken).Where(t => t.Length > 0).ToList();
}

public static class TextTokens
{
    private static readonly char[] s_whitespace = [' ', '\t', '\r', '\n', ' ', ' ', ' '];

    /// <summary>Whitespace-separated words, punctuation kept attached.</summary>
    public static IReadOnlyList<string> SplitWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(s_whitespace, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Effective-title rules: user title ?? auto title ?? first words ?? date.</summary>
public static class RecordingTitle
{
    public static string Effective(RecordingManifest manifest, int maxWords = RecordingManifest.FallbackTitleWords)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!string.IsNullOrWhiteSpace(manifest.Title))
            return manifest.Title.Trim();
        if (!string.IsNullOrWhiteSpace(manifest.AutoTitle))
            return manifest.AutoTitle.Trim();

        var fromText = FromText(manifest.Current?.Excerpt, maxWords);
        if (fromText is not null)
            return fromText;

        return "Recording " + manifest.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>First <paramref name="maxWords"/> words, with an ellipsis when cut; null for empty text.</summary>
    public static string? FromText(string? text, int maxWords = RecordingManifest.FallbackTitleWords)
    {
        var words = TextTokens.SplitWords(text);
        if (words.Count == 0)
            return null;
        if (words.Count <= maxWords)
            return string.Join(" ", words);

        var head = string.Join(" ", words.Take(maxWords)).TrimEnd(',', ';', ':', '-', '—', '.');
        return head + "…";
    }
}
