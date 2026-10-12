namespace SpeechLib.AudioStorage;

/// <summary>
/// A stretch of capture time that is NOT in the audio file (the user muted the
/// microphone). The file is continuous; the gap is only metadata.
/// </summary>
/// <param name="AtSample">File position (samples written before the gap).</param>
/// <param name="MutedSamples">Length of the muted stretch in capture samples.</param>
public readonly record struct RecordingGap(long AtSample, long MutedSamples)
{
    /// <summary>File position of the gap in seconds.</summary>
    public double AtSeconds(int sampleRate) => (double)AtSample / sampleRate;

    /// <summary>Muted duration in seconds.</summary>
    public double DurationSeconds(int sampleRate) => (double)MutedSamples / sampleRate;
}

/// <summary>
/// Sample counter shared by the stored audio and the transcript segments.
/// Segment times are file positions: muted batches are neither recognized nor
/// written, so the recognizer's sample count and the file's sample count stay equal.
/// Not thread-safe; owned by a single writer.
/// </summary>
public sealed class RecordingTimeline
{
    private readonly List<RecordingGap> _gaps = [];

    public RecordingTimeline(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Samples in the file so far.</summary>
    public long SamplesWritten { get; private set; }

    /// <summary>File position in seconds (= <see cref="SamplesWritten"/> / rate).</summary>
    public double Seconds => ToSeconds(SamplesWritten);

    /// <summary>Muted stretches, in file order. Adjacent mutes at the same position are merged.</summary>
    public IReadOnlyList<RecordingGap> Gaps => _gaps;

    /// <summary>Total muted capture time, in samples.</summary>
    public long MutedSamples { get; private set; }

    public double ToSeconds(long sample) => (double)sample / SampleRate;

    public long ToSample(double seconds) => (long)Math.Round(seconds * SampleRate);

    /// <summary>Wall-clock (capture) offset of a file position: file time + all mutes before it.</summary>
    public double CaptureSecondsAt(long fileSample)
    {
        long muted = 0;
        foreach (var gap in _gaps)
        {
            if (gap.AtSample > fileSample) break;
            muted += gap.MutedSamples;
        }
        return ToSeconds(fileSample + muted);
    }

    internal void Advance(int samples) => SamplesWritten += samples;

    internal void AddGap(long mutedSamples)
    {
        if (mutedSamples <= 0) return;
        MutedSamples += mutedSamples;
        if (_gaps.Count > 0 && _gaps[^1].AtSample == SamplesWritten)
            _gaps[^1] = _gaps[^1] with { MutedSamples = _gaps[^1].MutedSamples + mutedSamples };
        else
            _gaps.Add(new RecordingGap(SamplesWritten, mutedSamples));
    }
}
