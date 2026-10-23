using SpeechLib.Models;

namespace VoiceType.Uno.History;

/// <summary>
/// The first words of an utterance that really end the previous segment. A transducer writes the closing mark of a
/// sentence when it hears what follows, so after a silence closed the utterance the mark leads the next one
/// (<c>". She'll be alright"</c>); the rest of a word that the model emitted late leads it as well
/// (<c>"s are"</c> after <c>"able"</c>, <see cref="SpeechLib.StreamingResult.FinalContinuesPrevious"/>). Both belong to the
/// segment before, offline and live.
/// </summary>
internal static class SegmentContinuation
{
    /// <summary>
    /// Moves the part of <paramref name="text"/> (cleaned: single spaces) that continues the previous segment onto the last
    /// of <paramref name="segments"/>: the sentence marks in front of it and, when the recognizer said so
    /// (<paramref name="continuesPrevious"/>), its first word. Without a previous segment only marks are dropped (a word
    /// stays: it is the first one). A word that was only completed late moves the end of a segment with decoder times to the
    /// end of its piece; a mark has no speech of its own and leaves the times alone.
    /// </summary>
    /// <param name="decoderWords">The decoder's words of <paramref name="text"/>, the first one being the late piece when there is one.</param>
    /// <param name="offsetSeconds">Added to the decoder times (the stream's start on the audio timeline).</param>
    /// <param name="skippedDecoderWords">Leading decoder words that went with the moved part and no longer belong to the text.</param>
    /// <returns>The rest of the text.</returns>
    public static string MoveToPrevious(List<TranscriptSegment> segments, string text, bool continuesPrevious,
        IReadOnlyList<WordTiming>? decoderWords, double offsetSeconds, out int skippedDecoderWords)
    {
        skippedDecoderWords = 0;
        var length = segments.Count > 0 ? Length(text, continuesPrevious) : LeadingPunctuation(text);
        if (length == 0)
            return text;

        var piece = text[..length];
        var isWordPiece = piece.Any(char.IsLetterOrDigit);
        AppendToLast(segments, piece,
            isWordPiece && decoderWords is { Count: > 0 } ? Math.Round(decoderWords[0].EndSeconds + offsetSeconds, 3) : null);
        skippedDecoderWords = isWordPiece ? 1 : 0;
        return text[length..].TrimStart();
    }

    /// <summary>
    /// Appends <paramref name="continuation"/> (blanks removed) to the text and the last word of the last segment; a decoder
    /// segment also ends no earlier than <paramref name="endSeconds"/>. Nothing happens without a previous segment.
    /// </summary>
    public static void AppendToLast(List<TranscriptSegment> segments, string continuation, double? endSeconds = null)
    {
        var piece = continuation.Replace(" ", "", StringComparison.Ordinal);
        if (piece.Length == 0 || segments.Count == 0)
            return;

        var last = segments[^1];
        last.Text += piece;
        if (last.Words.Count > 0)
            last.Words[^1].Text += piece;
        if (endSeconds is { } end && end > last.End && last.WordTiming == WordTimingKinds.Decoder)
        {
            last.End = end;
            if (last.Words.Count > 0)
                last.Words[^1].End = Math.Max(last.Words[^1].End, end);
        }
    }

    private static int Length(string text, bool continuesPrevious)
    {
        var marks = LeadingPunctuation(text);
        if (marks > 0 || !continuesPrevious || text.Length == 0)
            return marks;
        var space = text.IndexOf(' ');
        return space < 0 ? text.Length : space + 1;
    }

    /// <summary>Length of the punctuation-only prefix (". ", "?", ". ,") of <paramref name="text"/>.</summary>
    private static int LeadingPunctuation(string text)
    {
        var i = 0;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] is '.' or ',' or '!' or '?' or ';' or ':' or '…' or '。' or '？' or '！' or '、'))
            i++;
        // Only a separate token: ".5 kg" or "...and" are left alone.
        return i > 0 && (i == text.Length || char.IsWhiteSpace(text[i - 1])) ? i : 0;
    }
}
