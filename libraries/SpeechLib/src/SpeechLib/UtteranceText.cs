using System.Text;
using System.Text.RegularExpressions;

namespace SpeechLib;

/// <summary>
/// Joins the texts of consecutive utterances (<see cref="StreamingResult.Final"/> and the open
/// <see cref="StreamingResult.Partial"/>) of an <see cref="IUtteranceStreamingRecognizer"/> into one transcript.
/// </summary>
/// <remarks>
/// A transducer decides the punctuation of a sentence when it hears what follows. When the recognizer's silence rule has
/// already committed the sentence, the closing mark (<c>.</c>, <c>?</c>, ...) arrives later and leads the next utterance
/// (<c>"You are so rude"</c> is reported as <c>". You are so rude"</c>). That mark belongs to the previous utterance, so the
/// joined text must not put a space in front of it: <c>"... tonight"</c> + <c>". You are so rude"</c> is
/// <c>"... tonight. You are so rude"</c>, exactly the text the recognizer produces without utterance bookkeeping.
/// </remarks>
public static partial class UtteranceText
{
    // Optional blanks and language tags, then a run of sentence marks that is a token of its own (".5 kg" and "...and" are not).
    [GeneratedRegex(@"^(?:\s|<\w{2,3}(?:-\w{2,4})?>)*[.,!?;:…。？！、]+(?=\s|<|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMark();

    /// <summary>
    /// True when <paramref name="text"/> starts with punctuation that is a token of its own (<c>". You are so rude"</c>,
    /// <c>"? &lt;en-US&gt; Yes"</c>): the closing mark of the previous utterance. Leading blanks and language tags are skipped.
    /// </summary>
    public static bool StartsWithMark(string? text) => !string.IsNullOrEmpty(text) && LeadingMark().IsMatch(text);

    /// <summary>
    /// Appends <paramref name="next"/> to <paramref name="previous"/>: with one space, or without any when
    /// <paramref name="continuation"/> says that it continues the previous text
    /// (<see cref="StreamingResult.FinalContinuesPrevious"/>) or it starts with the mark of the previous utterance
    /// (<see cref="StartsWithMark"/>). Empty input is skipped.
    /// </summary>
    public static string Join(string? previous, string? next, bool continuation = false)
    {
        if (string.IsNullOrWhiteSpace(next))
            return previous ?? "";
        var text = new StringBuilder(previous);
        Append(text, next, continuation);
        return text.ToString();
    }

    /// <summary>
    /// <see cref="Join(string?, string?, bool)"/> for a transcript that is built up in a <see cref="StringBuilder"/>:
    /// appends <paramref name="next"/> to <paramref name="transcript"/> with the same rule.
    /// </summary>
    public static void Append(StringBuilder transcript, string? next, bool continuation = false)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (string.IsNullOrWhiteSpace(next))
            return;

        while (transcript.Length > 0 && char.IsWhiteSpace(transcript[^1]))
            transcript.Length--;
        if (transcript.Length == 0)
        {
            transcript.Append(next);
            return;
        }

        if (!continuation && !StartsWithMark(next))
            transcript.Append(' ');
        transcript.Append(next.TrimStart());
    }

    /// <summary>Joins all <paramref name="utterances"/> in order with <see cref="Join(string?, string?, bool)"/>, recognizing a leading mark only.</summary>
    public static string Join(IEnumerable<string?> utterances)
    {
        ArgumentNullException.ThrowIfNull(utterances);
        var result = new StringBuilder();
        foreach (var utterance in utterances)
            Append(result, utterance);
        return result.ToString();
    }
}
