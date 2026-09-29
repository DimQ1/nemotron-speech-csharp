namespace SpeechLib.Audio;

/// <summary>
/// Mixes several live capture streams (microphone, one or more loopback devices) that
/// deliver audio in independently sized packets.
/// </summary>
/// <remarks>
/// Each source keeps a FIFO. A mix publishes only the samples every active source has
/// delivered, so streams stay aligned instead of being padded with silence on every
/// drain (padding each drain to the longest packet inserts gaps into the shorter stream
/// and makes the output run faster than real time).
/// <para>
/// WASAPI loopback delivers nothing while nothing is playing, so a source that has not
/// delivered for <c>idleDrains</c> mixes stops holding the others back. When the sources
/// drift apart by more than <c>maxSkewSamples</c> (a device stalled or its clock drifts),
/// everything buffered is published and the lagging source is padded once.
/// </para>
/// </remarks>
public sealed class MultiSourceMixer
{
    private sealed class Source
    {
        public float[] Buffer = new float[4096];
        public int Count;
        public int IdleMixes;
        public bool PushedSinceMix;
        public bool Removed;
        public float Gain = 1f;
    }

    private readonly List<Source> _sources = new();
    private readonly int _maxSkewSamples;
    private readonly int _idleMixes;

    /// <param name="maxSkewSamples">Largest backlog kept to wait for a lagging source (default 150 ms at 16 kHz).</param>
    /// <param name="idleMixes">Mixes without data after which a source no longer holds the others back.</param>
    public MultiSourceMixer(int maxSkewSamples = 2400, int idleMixes = 4)
    {
        _maxSkewSamples = Math.Max(1, maxSkewSamples);
        _idleMixes = Math.Max(1, idleMixes);
    }

    /// <summary>Adds a source and returns its index.</summary>
    public int AddSource(float gain = 1f)
    {
        _sources.Add(new Source { Gain = gain });
        return _sources.Count - 1;
    }

    /// <summary>A removed source (device stopped) is ignored from now on.</summary>
    public void RemoveSource(int index)
    {
        var source = _sources[index];
        source.Removed = true;
        source.Count = 0;
    }

    public void SetGain(int index, float gain) => _sources[index].Gain = gain;

    /// <summary>Appends samples delivered by a source.</summary>
    public void Push(int index, ReadOnlySpan<float> samples)
    {
        var source = _sources[index];
        if (source.Removed || samples.IsEmpty)
            return;

        var required = source.Count + samples.Length;
        if (source.Buffer.Length < required)
            Array.Resize(ref source.Buffer, Math.Max(required, source.Buffer.Length * 2));

        samples.CopyTo(source.Buffer.AsSpan(source.Count));
        source.Count = required;
        source.PushedSinceMix = true;
        source.IdleMixes = 0;
    }

    /// <summary>
    /// Returns the next mixed block (possibly empty) and consumes it from the sources.
    /// Samples are summed with the source gains and clamped to [-1, 1].
    /// </summary>
    public float[] Mix()
    {
        foreach (var source in _sources)
        {
            if (!source.PushedSinceMix)
                source.IdleMixes++;
            source.PushedSinceMix = false;
        }

        var min = int.MaxValue;
        var max = 0;
        foreach (var source in _sources)
        {
            if (!IsActive(source))
                continue;
            min = Math.Min(min, source.Count);
            max = Math.Max(max, source.Count);
        }

        if (max == 0)
            return Array.Empty<float>();

        var count = max - min > _maxSkewSamples ? max : min;
        if (count == 0)
            return Array.Empty<float>();

        var output = new float[count];
        foreach (var source in _sources)
        {
            if (source.Removed || source.Count == 0)
                continue;

            var take = Math.Min(count, source.Count);
            var gain = source.Gain;
            for (var i = 0; i < take; i++)
                output[i] += source.Buffer[i] * gain;

            source.Count -= take;
            if (source.Count > 0)
                Array.Copy(source.Buffer, take, source.Buffer, 0, source.Count);
        }

        for (var i = 0; i < output.Length; i++)
            output[i] = output[i] > 1f ? 1f : output[i] < -1f ? -1f : output[i];

        return output;
    }

    private bool IsActive(Source source) =>
        !source.Removed && (source.Count > 0 || source.IdleMixes < _idleMixes);
}
