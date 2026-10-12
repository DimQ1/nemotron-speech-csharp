namespace SpeechLib.AudioStorage;

/// <summary>
/// Incremental, crash-safe recording writer.
/// <para>
/// Audio goes to <see cref="PartPath"/> (<c>&lt;file&gt;.part</c>) and is flushed about once a
/// second, so a killed process loses at most ~1 s. <see cref="CompleteAsync"/> finalizes
/// the stream and renames it to <see cref="FinalPath"/>. Disposing without completing
/// leaves the <c>.part</c> file for <see cref="RecordingAudioRecovery.Recover"/>.
/// </para>
/// </summary>
public interface IRecordingAudioWriter : IAsyncDisposable
{
    RecordingAudioFormat Format { get; }

    /// <summary>Input sample rate (the recognizer rate, 16 kHz in practice).</summary>
    int SampleRate { get; }

    /// <summary>Path the finished file will have.</summary>
    string FinalPath { get; }

    /// <summary>Path being written while recording (<see cref="FinalPath"/> + <c>.part</c>).</summary>
    string PartPath { get; }

    /// <summary>Samples accepted so far (file position; drives segment start/end times).</summary>
    long SamplesWritten { get; }

    /// <summary>Muted stretches recorded with <see cref="AddGap"/>.</summary>
    IReadOnlyList<RecordingGap> Gaps { get; }

    /// <summary>Appends mono float samples in [-1, 1] at <see cref="SampleRate"/>.</summary>
    void Append(ReadOnlySpan<float> samples);

    /// <summary>
    /// Records that <paramref name="mutedSamples"/> of capture were dropped (muted) at the
    /// current position. Nothing is written to the file.
    /// </summary>
    void AddGap(long mutedSamples);

    /// <summary>Finalizes the stream, flushes to disk and renames <c>.part</c> → final.</summary>
    Task<RecordingAudioResult> CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a finished (or recovered) recording.</summary>
/// <param name="Path">Final file path.</param>
/// <param name="Format">Storage format.</param>
/// <param name="SampleRate">Sample rate of the samples the file represents.</param>
/// <param name="Samples">Sample count in the file (after Opus pre-skip/end trimming).</param>
/// <param name="Bytes">File size.</param>
/// <param name="Gaps">Muted stretches (empty for recovered files: gaps live in the manifest).</param>
/// <param name="Recovered">True when produced by <see cref="RecordingAudioRecovery"/> from a leftover <c>.part</c>.</param>
public sealed record RecordingAudioResult(
    string Path,
    RecordingAudioFormat Format,
    int SampleRate,
    long Samples,
    long Bytes,
    IReadOnlyList<RecordingGap> Gaps,
    bool Recovered = false)
{
    public double DurationSeconds => SampleRate == 0 ? 0 : (double)Samples / SampleRate;
}
