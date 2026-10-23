using SpeechLib.Models;

namespace SpeechLib.Nemotron;

/// <summary>
/// Turns the tokens of one utterance (emission frames on the stream timeline) into timed words: positions from the
/// calibrated emission lag, grouped by <see cref="TimedWordBuilder"/> (tags and punctuation never start or extend a
/// word), then put on the speech onsets and offsets of the audio around pauses.
/// </summary>
internal sealed class NemotronWordTimer
{
    private readonly NemotronVocabulary _vocabulary;
    private readonly NemotronWordTimingOptions _options;
    private readonly int _samplesPerFrame;
    private readonly int _sampleRate;
    private readonly int _chunkSamples;

    public NemotronWordTimer(NemotronVocabulary vocabulary, NemotronWordTimingOptions options, int samplesPerFrame, int sampleRate, int chunkSamples)
    {
        _vocabulary = vocabulary;
        _options = options;
        _samplesPerFrame = samplesPerFrame;
        _sampleRate = sampleRate;
        _chunkSamples = chunkSamples;
    }

    /// <summary>The lag that applies to words of <paramref name="language"/> (BCP-47 primary subtag or null).</summary>
    public NemotronLagCalibration LagFor(string? language) =>
        NemotronLagTable.Lookup(_chunkSamples, language, _options);

    /// <summary>
    /// Timed words of <paramref name="tokens"/>.
    /// </summary>
    /// <param name="tokens">Tokens in emission order.</param>
    /// <param name="language">Primary language subtag the lag is chosen for, or null.</param>
    /// <param name="paddingBefore">Samples of flush padding that precede a chunk on the grid (stream clock = grid clock minus this).</param>
    /// <param name="mask">Speech mask of the audio, or null to skip snapping.</param>
    /// <param name="fedSamples">Samples of the stream fed so far; times are clamped to it.</param>
    /// <param name="notBeforeSeconds">End of the last word of the utterance before, which the first words may not overlap.</param>
    public List<WordTiming> Build(IReadOnlyList<NemotronToken> tokens, string? language, Func<int, long> paddingBefore, NemotronSpeechMask? mask, long fedSamples,
        double notBeforeSeconds = 0)
    {
        if (tokens.Count == 0)
            return [];

        var lag = _options.Calibrate ? LagFor(language) : default;
        var lagStart = (long)Math.Round(lag.StartLagSeconds * _sampleRate);
        var lagEnd = (long)Math.Round(lag.EndLagSeconds * _sampleRate);
        var bias = _options.Calibrate ? (long)Math.Round(_options.LateBiasSeconds * _sampleRate) : 0;

        // 1. positions of every token: the word start is the first token's slot start minus the lag, a following token of the
        //    same word keeps its own slot; the last token's end is the slot end minus the end lag (the builder takes the
        //    end of the word from its last letter token).
        var raw = new List<TimedWordBuilder.RawToken>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var pad = paddingBefore(t.Chunk);
            var slotStart = t.GlobalFrame * _samplesPerFrame - pad;
            var slotEnd = slotStart + _samplesPerFrame;
            var start = Math.Max(0, slotStart - lagStart + bias);
            var end = Math.Max(start + 1, slotEnd - lagEnd);
            raw.Add(new TimedWordBuilder.RawToken(t.Id, start, end));
        }

        // A token's end must not run past the start of the token after it (the builder trims words, not tokens).
        for (var i = 0; i + 1 < raw.Count; i++)
        {
            if (raw[i].EndSample > raw[i + 1].StartSample)
                raw[i] = raw[i] with { EndSample = Math.Max(raw[i].StartSample + 1, raw[i + 1].StartSample) };
        }

        var words = TimedWordBuilder.Build(raw, _sampleRate, _vocabulary.StartsWord, _vocabulary.PieceOf);

        // Marks in front of the first word (the closer of the previous utterance, emitted after the silence rule had committed
        // it) are no speech of this utterance: they are not snapped and sit on the start of the first word, so that an
        // utterance (and the segment built from it) starts where its speech starts and not where the late mark was emitted.
        var marks = words.TakeWhile(w => !w.Word.Any(char.IsLetterOrDigit)).Count();
        if (marks < words.Count)
        {
            var speech = words.GetRange(marks, words.Count - marks);

            // 2. energy snapping of the words that follow / precede a pause
            if (mask is not null && _options.Snap is { } snap)
                Snap(speech, mask, snap);
            NotBefore(speech, notBeforeSeconds, _options.Snap?.MinWordSeconds ?? 0);

            var at = speech[0].StartSeconds;
            speech.InsertRange(0, words.Take(marks).Select(m => m with { StartSeconds = at, EndSeconds = at, Tokens = Retime(m.Tokens, at, at) }));
            words = speech;
        }
        else
        {
            NotBefore(words, notBeforeSeconds, 0);      // only marks: the closing mark of the sentence before, no speech of its own
        }

        // 3. inside the stream
        Clamp(words, fedSamples);
        return words;
    }

    // The first word after a pause starts where the speech does. The words behind it that the model showed in the same burst are
    // as early as it was (the emission lags less right after a pause than later), so they keep their spacing to it: they move
    // by the same amount instead of all taking the one onset the audio has. Words stay at least MinWordSeconds apart.
    private void Snap(List<WordTiming> words, NemotronSpeechMask mask, NemotronSnapOptions snap)
    {
        var onsets = new (double Seconds, long SilenceStartFrame)?[words.Count];
        for (var i = 0; i < words.Count; i++)
            onsets[i] = mask.FindOnset(words[i].StartSeconds, snap.MaxBackwardSeconds, snap.MaxForwardSeconds, snap.MinSilenceFrames);

        var starts = new double[words.Count];
        var endShifts = new double[words.Count];    // how far the start of a word that kept its spacing was moved (its end moves with it)
        var firstOfBurst = new bool[words.Count];   // the word took the speech onset itself
        var claimed = double.NegativeInfinity;      // the onset the last such word took
        var burstShift = 0.0;
        var previousStart = double.NegativeInfinity;
        for (var i = 0; i < words.Count; i++)
        {
            var start = words[i].StartSeconds;
            if (onsets[i] is { } onset)
            {
                if (onset.Seconds > claimed && onset.Seconds >= previousStart)
                {
                    start = onset.Seconds;
                    claimed = onset.Seconds;
                    burstShift = start - words[i].StartSeconds;
                    firstOfBurst[i] = true;
                }
                else if (onset.Seconds == claimed && burstShift > 0)
                {
                    start += burstShift;
                    endShifts[i] = burstShift;
                }
            }

            starts[i] = Math.Max(start, previousStart + snap.MinWordSeconds);
            previousStart = starts[i];
        }

        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var start = starts[i];
            var end = w.EndSeconds + endShifts[i];

            // The end: the speech offset in front of the silence that ends with the next word's snapped onset; the last word
            // takes the nearest speech offset followed by a pause. Never behind the next word's start.
            if (i + 1 < words.Count)
            {
                if (firstOfBurst[i + 1] && onsets[i + 1] is { } next)
                {
                    var offset = next.SilenceStartFrame * 0.01;
                    if (Math.Abs(offset - end) <= snap.EndWindowSeconds && offset > start)
                        end = offset;
                }
                end = Math.Min(end, starts[i + 1]);
            }
            else if (mask.FindOffset(end, snap.EndWindowSeconds, snap.LastEndMaxForwardSeconds, snap.MinSilenceFrames) is { } tail && tail > start)
            {
                end = tail;
            }

            end = Math.Max(end, start + snap.MinWordSeconds);
            if (i + 1 < words.Count)
                end = Math.Min(end, starts[i + 1]);
            words[i] = w with { StartSeconds = start, EndSeconds = end, Tokens = Retime(w.Tokens, start, end) };
        }
    }

    // The words of an utterance start where the words of the one before end: the two are timed separately, so a word that follows
    // right behind a Final can come out earlier than the end of that Final's last word. Late is harmless: the start moves forward,
    // the words behind it follow as far as they overlap.
    private static void NotBefore(List<WordTiming> words, double notBefore, double minWordSeconds)
    {
        var floor = notBefore;
        for (var i = 0; i < words.Count && words[i].StartSeconds < floor; i++)
        {
            var w = words[i];
            var end = Math.Max(w.EndSeconds, floor + minWordSeconds);
            words[i] = w with { StartSeconds = floor, EndSeconds = end, Tokens = Retime(w.Tokens, floor, end) };
            floor = end;
        }
    }

    private void Clamp(List<WordTiming> words, long fedSamples)
    {
        var limit = (double)fedSamples / _sampleRate;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var start = Math.Clamp(w.StartSeconds, 0, limit);
            var end = Math.Clamp(w.EndSeconds, start, limit);
            if (start == w.StartSeconds && end == w.EndSeconds)
                continue;
            words[i] = w with { StartSeconds = start, EndSeconds = end, Tokens = Retime(w.Tokens, start, end) };
        }
    }

    // Token times follow the word: the first token starts with the word, the others stay inside it.
    private static IReadOnlyList<TokenTiming>? Retime(IReadOnlyList<TokenTiming>? tokens, double start, double end)
    {
        if (tokens is null || tokens.Count == 0)
            return tokens;
        var result = new TokenTiming[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var s = i == 0 ? start : Math.Clamp(t.StartSeconds, start, end);
            var e = Math.Clamp(Math.Max(t.EndSeconds, s), s, Math.Max(end, s));
            result[i] = t with { StartSeconds = s, EndSeconds = e };
        }
        return result;
    }
}
