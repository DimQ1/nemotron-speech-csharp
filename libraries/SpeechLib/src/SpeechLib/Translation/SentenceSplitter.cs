using System.Text;

namespace SpeechLib.Translation;

/// <summary>
/// Incremental sentence segmentation for streaming text. Feeds a growing
/// transcript and yields only fully-terminated sentences, leaving the
/// incomplete tail in the buffer for the next call.
/// </summary>
public static class SentenceSplitter
{
    private static readonly char[] Terminators = ['.', '!', '?', '…'];

    /// <summary>
    /// Extracts all complete sentences from <paramref name="buffer"/> starting at
    /// <paramref name="consumed"/>. Advances <paramref name="consumed"/> past each
    /// extracted sentence (including any following whitespace). The incomplete tail
    /// is left untouched for the next call.
    /// </summary>
    public static List<string> ExtractCompleteSentences(StringBuilder buffer, ref int consumed)
    {
        var sentences = new List<string>();
        if (buffer.Length == 0)
            return sentences;

        int searchFrom = Math.Max(0, consumed);
        if (searchFrom >= buffer.Length)
            return sentences;

        int segmentStart = searchFrom;
        for (int i = searchFrom; i < buffer.Length; i++)
        {
            if (Array.IndexOf(Terminators, buffer[i]) < 0)
                continue;

            // A terminator closes a sentence only when followed by whitespace or
            // the end of the buffer (so "3.14" or "Mr. Smith" are not split).
            int next = i + 1;
            if (next < buffer.Length && !char.IsWhiteSpace(buffer[next]))
                continue;

            int end = next; // exclusive index just past the terminator
            var sentence = buffer.ToString(segmentStart, end - segmentStart).Trim();
            if (sentence.Length > 0)
                sentences.Add(sentence);

            // Skip following whitespace so the next sentence starts clean.
            int k = end;
            while (k < buffer.Length && char.IsWhiteSpace(buffer[k]))
                k++;

            consumed = k;
            segmentStart = k;
            i = k - 1; // loop increment moves i to k
        }

        return sentences;
    }
}
