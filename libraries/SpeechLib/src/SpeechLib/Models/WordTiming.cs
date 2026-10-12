namespace SpeechLib.Models;

/// <summary>
/// A single word with its start and end time in the audio stream.
/// </summary>
public sealed record WordTiming
{
    /// <summary>The word text.</summary>
    public string Word { get; init; } = "";

    /// <summary>Start time of the word in seconds.</summary>
    public double StartSeconds { get; init; }

    /// <summary>End time of the word in seconds.</summary>
    public double EndSeconds { get; init; }

    /// <summary>
    /// The decoder tokens the word is made of (exact decoder alignment), or null when the
    /// timing was not derived from tokens.
    /// </summary>
    public IReadOnlyList<TokenTiming>? Tokens { get; init; }
}

/// <summary>
/// One decoded token with its position on the absolute timeline of the audio stream
/// (seconds from the first sample fed after the last reset).
/// </summary>
/// <param name="Id">Vocabulary id.</param>
/// <param name="Text">Detokenized piece (SentencePiece '▁' already turned into a space).</param>
/// <param name="StartSeconds">Start of the encoder frame that emitted the token.</param>
/// <param name="EndSeconds">End of the token's duration (never before <paramref name="StartSeconds"/> + one sample).</param>
public readonly record struct TokenTiming(int Id, string Text, double StartSeconds, double EndSeconds);

/// <summary>
/// Groups decoded tokens into words with the timing of their first and last token.
/// A word starts at a token carrying the SentencePiece '▁' marker; a group without a letter
/// or digit (punctuation) joins the previous word, matching the detokenized text.
/// </summary>
public static class TimedWordBuilder
{
    /// <summary>A token with sample positions on the stream timeline.</summary>
    public readonly record struct RawToken(int Id, long StartSample, long EndSample);

    public static List<WordTiming> Build(
        IReadOnlyList<RawToken> tokens,
        int sampleRate,
        Func<int, bool> startsWord,
        Func<int, string> pieceOf)
    {
        var words = new List<WordTiming>();
        int i = 0;
        while (i < tokens.Count)
        {
            int j = i + 1;
            while (j < tokens.Count && !startsWord(tokens[j].Id))
                j++;

            var parts = new List<TokenTiming>(j - i);
            var text = new System.Text.StringBuilder();
            for (int k = i; k < j; k++)
            {
                var piece = pieceOf(tokens[k].Id);
                text.Append(piece);
                long end = Math.Max(tokens[k].EndSample, tokens[k].StartSample + 1);
                parts.Add(new TokenTiming(tokens[k].Id, piece, (double)tokens[k].StartSample / sampleRate, (double)end / sampleRate));
            }

            var word = text.ToString().Trim();
            if (word.Length > 0)
            {
                bool lexical = word.Any(char.IsLetterOrDigit);
                if (!lexical && words.Count > 0)
                {
                    var prev = words[^1];
                    words[^1] = prev with
                    {
                        Word = prev.Word + word,
                        EndSeconds = prev.EndSeconds, // a mark does not extend the word's speech
                        Tokens = prev.Tokens!.Concat(parts).ToList(),
                    };
                }
                else
                {
                    words.Add(new WordTiming
                    {
                        Word = word,
                        StartSeconds = parts[0].StartSeconds,
                        // The end of the last token that carries a letter or digit: the duration the
                        // decoder gives a trailing mark ('.') typically runs on into the pause after it.
                        EndSeconds = parts.LastOrDefault(p => p.Text.Any(char.IsLetterOrDigit) is true) is { Text: not null } lex
                            ? lex.EndSeconds
                            : parts[^1].EndSeconds,
                        Tokens = parts,
                    });
                }
            }
            i = j;
        }

        // A token's duration can overshoot into the next word: words never overlap.
        for (int w = 0; w + 1 < words.Count; w++)
        {
            if (words[w].EndSeconds > words[w + 1].StartSeconds)
                words[w] = words[w] with { EndSeconds = Math.Max(words[w].StartSeconds, words[w + 1].StartSeconds) };
        }
        return words;
    }
}
