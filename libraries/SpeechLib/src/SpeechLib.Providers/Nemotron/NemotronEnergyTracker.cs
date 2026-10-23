namespace SpeechLib.Nemotron;

/// <summary>
/// Energy history of the audio a recognizer was fed: one level in dBFS per 10 ms frame, kept in a ring, and the
/// speech/silence mask derived from it. Used to put the word times of the Nemotron emission frames (80 ms steps that
/// lag the sound by 0.2 to 0.3 s) onto the real speech onsets and offsets after a pause.
/// </summary>
/// <remarks>
/// The detector is the one of <see cref="SpeechLib.Models.TokenTimingRefiner"/> (a frame is voiced when it is
/// <c>MarginDb</c> above the noise floor, gaps and blips shorter than 3 frames are bridged) with a floor taken from a low
/// percentile of the whole ring instead of the 15th percentile of one utterance: on dense dialogue the 15th percentile
/// lands inside quiet speech and invents pauses. Without a clear speech-to-noise range
/// (<see cref="NemotronSnapOptions.MinSpeechToNoiseDb"/>) there is no mask and nothing is snapped.
/// </remarks>
internal sealed class NemotronEnergyTracker
{
    /// <summary>Samples per energy frame (10 ms at 16 kHz).</summary>
    public const int FrameSamples = 160;

    private const int MinRunFrames = 3;
    private const int MinFramesForMask = 20;

    private readonly float[] _db;
    private readonly int _capacity;
    private long _frames;          // completed frames since the reset
    private double _sum;           // sum of squares of the frame being filled
    private int _filled;           // samples in it

    public NemotronEnergyTracker(double historySeconds, int sampleRate)
    {
        if (historySeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(historySeconds));
        _capacity = Math.Max(MinFramesForMask, (int)Math.Ceiling(historySeconds * sampleRate / FrameSamples));
        _db = new float[_capacity];
    }

    /// <summary>Completed 10 ms frames since the last reset.</summary>
    public long FrameCount => _frames;

    /// <summary>First frame that is still in the ring.</summary>
    public long FirstFrame => Math.Max(0, _frames - _capacity);

    public void Reset()
    {
        _frames = 0;
        _sum = 0;
        _filled = 0;
    }

    /// <summary>Adds samples of the stream (every sample fed, including the ones the silence gate drops).</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        var i = 0;
        while (i < samples.Length)
        {
            var take = Math.Min(FrameSamples - _filled, samples.Length - i);
            double sum = 0;
            for (var k = 0; k < take; k++)
            {
                double x = samples[i + k];
                sum += x * x;
            }
            _sum += sum;
            _filled += take;
            i += take;
            if (_filled == FrameSamples)
            {
                _db[(int)(_frames % _capacity)] = (float)(10 * Math.Log10(_sum / FrameSamples + 1e-12));
                _frames++;
                _sum = 0;
                _filled = 0;
            }
        }
    }

    // The level of the speech in the ring: a high percentile of the frames that stand out of the noise floor, not of all frames.
    // How much of the ring is speech varies from a conversation (a third) to a dictation (a sentence a minute, a few percent), and
    // the 95th percentile of ALL frames is a noise frame as soon as speech is less than 5 % of them. Where speech is that common
    // the two are the same measure (the percentile of the speech frames that sits at the 95th of all frames, 0.75 to 0.9 for
    // speech fractions of 0.2 to 0.5). Without 20 frames above the floor there is no speech at all: noise alone stays at about
    // 10 dB over its floor.
    private static double SpeechLevel(float[] sorted, double noise, NemotronSnapOptions options)
    {
        var floor = noise + options.SpeechCandidateMarginDb;
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] > floor)
                high = middle;
            else
                low = middle + 1;
        }

        var candidates = sorted.Length - low;
        return candidates < MinFramesForMask ? noise : sorted[low + (int)(candidates * options.SpeechLevelPercentile)];
    }

    /// <summary>
    /// The speech mask of the frames in the ring, or null when the audio has no clear speech/noise range yet.
    /// </summary>
    public NemotronSpeechMask? Snapshot(NemotronSnapOptions options)
    {
        var first = FirstFrame;
        var count = (int)(_frames - first);
        if (count < MinFramesForMask)
            return null;

        var levels = new float[count];
        for (var k = 0; k < count; k++)
            levels[k] = _db[(int)((first + k) % _capacity)];
        var sorted = (float[])levels.Clone();
        Array.Sort(sorted);

        double noise = sorted[(int)(count * options.NoiseFloorPercentile)];
        if (SpeechLevel(sorted, noise, options) - noise < options.MinSpeechToNoiseDb)
            return null;

        var threshold = Math.Max(noise + options.MarginDb, options.MinThresholdDb);
        var voiced = new bool[count];
        for (var k = 0; k < count; k++)
            voiced[k] = levels[k] > threshold;

        // bridge short gaps (not at the edges), then drop short blips
        for (var f = 0; f < count;)
        {
            if (voiced[f]) { f++; continue; }
            var g = f;
            while (g < count && !voiced[g]) g++;
            if (f > 0 && g < count && g - f < MinRunFrames)
                for (var k = f; k < g; k++) voiced[k] = true;
            f = g;
        }
        for (var f = 0; f < count;)
        {
            if (!voiced[f]) { f++; continue; }
            var g = f;
            while (g < count && voiced[g]) g++;
            if (g - f < MinRunFrames)
                for (var k = f; k < g; k++) voiced[k] = false;
            f = g;
        }
        return new NemotronSpeechMask(voiced, first);
    }
}

/// <summary>Speech/silence flags per 10 ms frame of a stretch of the stream.</summary>
internal sealed class NemotronSpeechMask
{
    private readonly bool[] _voiced;

    public NemotronSpeechMask(bool[] voiced, long firstFrame)
    {
        _voiced = voiced;
        FirstFrame = firstFrame;
    }

    /// <summary>Stream frame of the first flag.</summary>
    public long FirstFrame { get; }

    /// <summary>Number of frames covered.</summary>
    public int Count => _voiced.Length;

    /// <summary>The speech onset (first voiced frame) closest to <paramref name="seconds"/> within the window that is preceded by at least <paramref name="minSilenceFrames"/> unvoiced frames.</summary>
    /// <param name="seconds">Where the onset is expected.</param>
    /// <param name="before">How far before it an onset may lie, in seconds.</param>
    /// <param name="after">How far after it an onset may lie, in seconds.</param>
    /// <param name="minSilenceFrames">Unvoiced frames that must precede the onset.</param>
    /// <returns>The onset in seconds and the frame the silence before it began, or null.</returns>
    public (double Seconds, long SilenceStartFrame)? FindOnset(double seconds, double before, double after, int minSilenceFrames)
    {
        var lo = (int)Math.Max(1, Math.Ceiling((seconds - before) * 100 - FirstFrame));
        var hi = (int)Math.Min(_voiced.Length - 1, Math.Floor((seconds + after) * 100 - FirstFrame));
        (double, long)? best = null;
        var bestDistance = double.MaxValue;
        for (var f = lo; f <= hi; f++)
        {
            if (!_voiced[f] || _voiced[f - 1])
                continue;
            var silence = 0;
            for (var k = f - 1; k >= 0 && !_voiced[k]; k--)
                silence++;
            if (silence < minSilenceFrames)
                continue;
            var t = (FirstFrame + f) * 0.01;
            var distance = Math.Abs(t - seconds);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (t, FirstFrame + f - silence);
            }
        }
        return best;
    }

    /// <summary>The speech offset (first unvoiced frame after a voiced run) closest to <paramref name="seconds"/> that is followed by at least <paramref name="minSilenceFrames"/> unvoiced frames.</summary>
    public double? FindOffset(double seconds, double before, double after, int minSilenceFrames)
    {
        var lo = (int)Math.Max(1, Math.Ceiling((seconds - before) * 100 - FirstFrame));
        var hi = (int)Math.Min(_voiced.Length - 1, Math.Floor((seconds + after) * 100 - FirstFrame));
        double? best = null;
        var bestDistance = double.MaxValue;
        for (var f = lo; f <= hi; f++)
        {
            if (_voiced[f] || !_voiced[f - 1])
                continue;
            var silence = 0;
            for (var k = f; k < _voiced.Length && !_voiced[k]; k++)
                silence++;
            if (silence < minSilenceFrames)
                continue;
            var t = (FirstFrame + f) * 0.01;
            var distance = Math.Abs(t - seconds);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = t;
            }
        }
        return best;
    }
}
