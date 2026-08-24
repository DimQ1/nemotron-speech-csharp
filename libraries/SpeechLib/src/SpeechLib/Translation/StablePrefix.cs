namespace SpeechLib.Translation;

/// <summary>
/// Stability analysis for streaming translation. Determines the longest prefix shared
/// by two successive provisional translations that ends on a word boundary, so that
/// the shared prefix can be treated as stable ("locked") while only the divergent
/// suffix stays provisional. Used by incremental translation to commit a translation
/// as soon as the current position is confirmed stable, instead of waiting for the
/// whole sentence to terminate.
/// </summary>
public static class StablePrefix
{
    /// <summary>
    /// Returns the longest common prefix of <paramref name="previous"/> and
    /// <paramref name="current"/> that ends at a word boundary (whitespace or
    /// punctuation). Returns an empty string when the common prefix spans fewer than
    /// <paramref name="minWords"/> complete words.
    /// </summary>
    public static string LongestWordAlignedCommonPrefix(string previous, string current, int minWords = 2)
    {
        if (minWords <= 0)
            throw new ArgumentOutOfRangeException(nameof(minWords));
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
            return string.Empty;

        int n = Math.Min(previous.Length, current.Length);
        int lockEnd = 0;   // exclusive index of the longest word-aligned shared prefix
        int words = 0;     // complete words inside the shared prefix
        bool inWord = false;

        int i;
        for (i = 0; i < n; i++)
        {
            if (previous[i] != current[i])
                break;

            if (IsBoundary(previous[i]))
            {
                if (inWord)
                {
                    words++;
                    inWord = false;
                }
                lockEnd = i + 1;
            }
            else
            {
                inWord = true;
            }
        }

        // The shared prefix consumed the shorter string entirely. The trailing word
        // counts as complete when the strings are identical, or when the longer string
        // continues with a word boundary right after the shared prefix.
        if (i == n)
        {
            char? next = previous.Length > n ? previous[n]
                       : current.Length > n ? current[n]
                       : null;

            if (next is null)
            {
                if (inWord)
                    words++;
                lockEnd = n;
            }
            else if (inWord && IsBoundary(next.Value))
            {
                words++;
                lockEnd = n;
            }
        }

        return words >= minWords ? previous.Substring(0, lockEnd) : string.Empty;
    }

    private static bool IsBoundary(char c)
    {
        if (char.IsWhiteSpace(c))
            return true;

        return c is '.' or '!' or '?' or '…' or ',' or ';' or ':'
                 or ')' or ']' or '}' or '"' or '\''
                 or '\u201C' or '\u201D' or '\u00AB' or '\u00BB';
    }
}
