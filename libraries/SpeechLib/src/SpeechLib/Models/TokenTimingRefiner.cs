namespace SpeechLib.Models;

/// <summary>
/// Moves the token positions of a transducer decoder onto the speech energy of the audio they
/// came from. A TDT decoder emits a token at the encoder frame (80 ms) where its joint network
/// decides, not where the sound begins: the first word after a pause is often emitted up to
/// ~300 ms before the speech starts, the last word of a sentence ~200-300 ms after it starts,
/// and a token's duration runs on into the following silence. Using a plain energy gate
/// (10 ms frames, 12 dB above the local noise floor) the refiner
/// <list type="bullet">
/// <item>moves the start of a word's first token forward to the first voiced frame when it was
/// emitted in silence (and the token's span reaches speech),</item>
/// <item>moves a word start emitted late back to the beginning of the voiced run it lies in,
/// when that run began after the previous token started (a real pause before the word),</item>
/// <item>cuts a token's end at the last voiced frame of its span.</item>
/// </list>
/// Nothing moves further than <see cref="MaxShiftSamples"/>, tokens stay ordered, and audio
/// without a clear speech / noise dynamic range (&lt; 12 dB) is left alone.
/// </summary>
public static class TokenTimingRefiner
{
    private const int FrameSamples = 160;      // 10 ms at 16 kHz
    private const double MarginDb = 12;
    private const double MinDb = -60;
    private const int MinRunFrames = 3;        // voiced runs / gaps shorter than this are merged / dropped
    private const int EndPadFrames = 2;

    /// <summary>Largest start / end shift in samples (400 ms at 16 kHz).</summary>
    public const int MaxShiftSamples = 6400;

    /// <param name="tokens">Tokens in stream order; positions in absolute samples.</param>
    /// <param name="startsWord">True for ids that begin a word.</param>
    /// <param name="audio">The audio the tokens lie in.</param>
    /// <param name="audioStartSample">Absolute sample of <paramref name="audio"/>[0].</param>
    /// <param name="previousStartSample">Start of the token before <paramref name="tokens"/>[0] (long.MinValue when none).</param>
    public static List<TimedWordBuilder.RawToken> Refine(
        IReadOnlyList<TimedWordBuilder.RawToken> tokens,
        Func<int, bool> startsWord,
        ReadOnlySpan<float> audio,
        long audioStartSample,
        long previousStartSample = long.MinValue)
    {
        var result = new List<TimedWordBuilder.RawToken>(tokens);
        if (tokens.Count == 0)
            return result;
        var voiced = VoicedFrames(audio);
        if (voiced is null)
            return result;

        long lastStart = previousStartSample;
        for (int i = 0; i < result.Count; i++)
        {
            var t = result[i];
            long s = t.StartSample, e = t.EndSample;
            int fs = Frame(s - audioStartSample, voiced.Length);
            int fe = Math.Clamp((int)((e - audioStartSample + FrameSamples - 1) / FrameSamples), fs + 1, voiced.Length);

            if (startsWord(t.Id) && fs < voiced.Length)
            {
                if (!voiced[fs])
                {
                    // Emitted in silence: the word starts at the first voiced frame of the token's span.
                    int v = FirstVoiced(voiced, fs, Math.Min(voiced.Length, Math.Max(fe, fs + MaxShiftSamples / FrameSamples)));
                    if (v >= 0)
                        s = audioStartSample + (long)v * FrameSamples;
                }
                else
                {
                    // Emitted inside speech: a word that follows a pause starts where its voiced run began.
                    int r0 = fs;
                    while (r0 > 0 && voiced[r0 - 1]) r0--;
                    long runStart = audioStartSample + (long)r0 * FrameSamples;
                    if (r0 < fs && s - runStart <= MaxShiftSamples && runStart > lastStart)
                        s = runStart;
                }
            }

            int lastV = LastVoiced(voiced, Frame(s - audioStartSample, voiced.Length), fe);
            if (lastV >= 0)
            {
                long cut = audioStartSample + (long)(lastV + 1 + EndPadFrames) * FrameSamples;
                if (cut < e && e - cut <= 4 * MaxShiftSamples)
                    e = cut;
            }

            s = Math.Max(s, lastStart == long.MinValue ? s : lastStart);
            e = Math.Max(e, s + FrameSamples);
            result[i] = t with { StartSample = s, EndSample = e };
            lastStart = s;
        }
        return result;
    }

    private static int Frame(long offsetSamples, int frames) =>
        (int)Math.Clamp(offsetSamples / FrameSamples, 0, Math.Max(0, frames - 1));

    private static int FirstVoiced(bool[] voiced, int from, int to)
    {
        for (int f = Math.Max(0, from); f < Math.Min(to, voiced.Length); f++)
            if (voiced[f]) return f;
        return -1;
    }

    private static int LastVoiced(bool[] voiced, int from, int to)
    {
        for (int f = Math.Min(to, voiced.Length) - 1; f >= Math.Max(0, from); f--)
            if (voiced[f]) return f;
        return -1;
    }

    /// <summary>Voiced flags per 10 ms frame, or null when the audio has no clear speech / noise range.</summary>
    internal static bool[]? VoicedFrames(ReadOnlySpan<float> audio)
    {
        int frames = audio.Length / FrameSamples;
        if (frames < 20)
            return null;
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            var frame = audio.Slice(f * FrameSamples, FrameSamples);
            foreach (var x in frame)
                sum += (double)x * x;
            db[f] = 10 * Math.Log10(sum / FrameSamples + 1e-12);
        }
        var sorted = (double[])db.Clone();
        Array.Sort(sorted);
        double noise = sorted[(int)(frames * 0.15)];
        double loud = sorted[(int)(frames * 0.95)];
        if (loud - noise < MarginDb)
            return null;

        double threshold = Math.Max(noise + MarginDb, MinDb);
        var voiced = new bool[frames];
        for (int f = 0; f < frames; f++)
            voiced[f] = db[f] > threshold;

        // Bridge short gaps, then drop short blips.
        for (int f = 0; f < frames;)
        {
            if (voiced[f]) { f++; continue; }
            int g = f;
            while (g < frames && !voiced[g]) g++;
            if (f > 0 && g < frames && g - f < MinRunFrames)
                for (int k = f; k < g; k++) voiced[k] = true;
            f = g;
        }
        for (int f = 0; f < frames;)
        {
            if (!voiced[f]) { f++; continue; }
            int g = f;
            while (g < frames && voiced[g]) g++;
            if (g - f < MinRunFrames)
                for (int k = f; k < g; k++) voiced[k] = false;
            f = g;
        }
        return voiced;
    }
}
