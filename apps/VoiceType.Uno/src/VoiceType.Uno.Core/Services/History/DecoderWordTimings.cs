using SpeechLib.Models;

namespace VoiceType.Uno.History;

/// <summary>
/// Maps the exact word timings of a recognizer's decoder (<see cref="WordTiming"/>, from
/// <see cref="SpeechLib.ITimedUtteranceRecognizer"/>) onto the words of a segment's cleaned
/// text (<see cref="TranscriptWord"/>, <c>wordTiming: "decoder"</c>). The recognizer groups
/// tokens into words itself, so the mapping only succeeds when both sides agree on the
/// words; otherwise the caller falls back to estimated timings.
/// </summary>
public static class DecoderWordTimings
{
    /// <summary>
    /// Words of <paramref name="textWords"/> with the decoder's times, or null when the two
    /// do not line up one to one.
    /// </summary>
    /// <param name="decoderWords">Timed words in stream seconds.</param>
    /// <param name="textWords">The words of the segment text (whitespace split).</param>
    /// <param name="offsetSeconds">Added to every decoder time (the stream's start on the audio timeline).</param>
    /// <param name="skipLeadingWords">
    /// Decoder words that belong to the previous segment and are not part of the text (the rest of a word that was
    /// completed late), in front of the marks that are skipped by themselves.
    /// </param>
    public static List<TranscriptWord>? Map(
        IReadOnlyList<WordTiming>? decoderWords, IReadOnlyList<string> textWords, double offsetSeconds = 0, int skipLeadingWords = 0)
    {
        if (skipLeadingWords > 0 && decoderWords is not null)
            decoderWords = decoderWords.Skip(skipLeadingWords).ToList();
        if (decoderWords is null || decoderWords.Count == 0 || textWords.Count == 0)
            return null;

        // Text and decoder words one to one.
        if (decoderWords.Count == textWords.Count)
            return Build(decoderWords, 0, textWords, offsetSeconds);

        // The text lost a leading mark that was moved to the previous segment.
        var skip = 0;
        while (skip < decoderWords.Count && !HasLexical(decoderWords[skip].Word))
            skip++;
        if (skip > 0 && decoderWords.Count - skip == textWords.Count)
            return Build(decoderWords, skip, textWords, offsetSeconds);

        // A detached mark in the text ("hello .") belongs to the previous word.
        var merged = new List<string>(textWords.Count);
        foreach (var word in textWords)
        {
            if (merged.Count > 0 && !HasLexical(word))
                merged[^1] += word;
            else
                merged.Add(word);
        }
        if (merged.Count != textWords.Count && merged.Count == decoderWords.Count)
            return Build(decoderWords, 0, merged, offsetSeconds);
        if (merged.Count != textWords.Count && decoderWords.Count - skip == merged.Count)
            return Build(decoderWords, skip, merged, offsetSeconds);

        return null;
    }

    private static List<TranscriptWord> Build(
        IReadOnlyList<WordTiming> decoderWords, int skip, IReadOnlyList<string> textWords, double offset)
    {
        var result = new List<TranscriptWord>(textWords.Count);
        for (var i = 0; i < textWords.Count; i++)
        {
            var d = decoderWords[skip + i];
            var start = Round(d.StartSeconds + offset);
            var end = Math.Max(start, Round(d.EndSeconds + offset));
            result.Add(new TranscriptWord(textWords[i], start, end));
        }
        return result;
    }

    private static bool HasLexical(string word) => word.Any(char.IsLetterOrDigit);

    private static double Round(double value) => Math.Round(value, 3);
}
