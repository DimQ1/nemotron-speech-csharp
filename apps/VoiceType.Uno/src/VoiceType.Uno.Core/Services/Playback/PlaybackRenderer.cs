namespace VoiceType.Uno.Services.Playback;

/// <summary>Seekable mono sample source (the app adapts <c>SpeechLib.AudioStorage.RecordingAudioStream</c>).</summary>
public interface IPlaybackSource
{
    int SampleRate { get; }

    /// <summary>Total length in samples.</summary>
    long Length { get; }

    void Seek(long sample);

    /// <summary>Fills the span; returns the count, 0 at the end.</summary>
    int Read(Span<float> buffer);
}

/// <summary>
/// The platform-independent half of a player: pulls samples from a <see cref="IPlaybackSource"/>
/// through a <see cref="WsolaTimeStretcher"/> for the output device and maps "frames the device
/// has played" back to a position in the recording.
/// <para>
/// Thread-safe: the device thread calls <see cref="Render"/> while the UI changes
/// <see cref="Speed"/> and a timer reads <see cref="SourcePositionAt"/>.
/// </para>
/// </summary>
public sealed class PlaybackRenderer
{
    private readonly object _sync = new();
    private readonly IPlaybackSource _source;
    private readonly WsolaTimeStretcher _stretcher;
    private long _startSample;

    public PlaybackRenderer(IPlaybackSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _stretcher = new WsolaTimeStretcher(source.SampleRate, source.Read);
        Restart(0);
    }

    public int SampleRate => _source.SampleRate;

    public long Length => _source.Length;

    public TimeSpan Duration => ToTime(Length);

    /// <summary>Playback speed; changes take effect within 20 ms, without a restart.</summary>
    public double Speed
    {
        get { lock (_sync) return _stretcher.Speed; }
        set { lock (_sync) _stretcher.Speed = Math.Clamp(value, PlaybackCursor.MinSpeed, PlaybackCursor.MaxSpeed); }
    }

    /// <summary>Real (non-padding) frames handed to the device since the last <see cref="Restart"/>.</summary>
    public long RenderedFrames
    {
        get { lock (_sync) return _stretcher.OutputPosition; }
    }

    /// <summary>True once the recording has been rendered to its end.</summary>
    public bool SourceEnded
    {
        get { lock (_sync) return _stretcher.IsFinished; }
    }

    /// <summary>Repositions at <paramref name="sample"/>; the next rendered frame is that sample. Call with the device stopped.</summary>
    public void Restart(long sample)
    {
        lock (_sync)
        {
            _startSample = Math.Clamp(sample, 0, _source.Length);
            _source.Seek(_startSample);
            _stretcher.Reset(_startSample);
        }
    }

    /// <summary>
    /// Fills <paramref name="output"/> completely: audio, then silence after the end.
    /// Returns the number of real audio frames.
    /// </summary>
    public int Render(Span<float> output)
    {
        int n;
        lock (_sync)
            n = _stretcher.Read(output);
        output[n..].Clear();
        return n;
    }

    /// <summary>Recording position (samples) of the frame the device has played <paramref name="playedFrames"/> frames after the last restart.</summary>
    public long SourcePositionAt(long playedFrames)
    {
        lock (_sync)
        {
            if (playedFrames <= 0) return _startSample;
            long rendered = _stretcher.OutputPosition;
            double position = _stretcher.InputPositionAt(Math.Min(playedFrames, rendered));
            if (playedFrames >= rendered && _stretcher.IsFinished) position = _source.Length;
            return (long)Math.Clamp(Math.Round(position), 0, _source.Length);
        }
    }

    public TimeSpan ToTime(long samples) => TimeSpan.FromSeconds((double)samples / _source.SampleRate);

    public long ToSamples(TimeSpan time) => (long)Math.Round(time.TotalSeconds * _source.SampleRate);
}
