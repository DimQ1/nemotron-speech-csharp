using SpeechLib.AudioStorage;

namespace VoiceType.Uno.Services.Playback;

/// <summary>
/// <see cref="IAudioPlayer"/> over a platform <see cref="IPcmOutput"/>.
/// <para>
/// Decoding streams from disk (<see cref="RecordingAudioStream"/>: one Ogg page at a time, seek
/// by page index), so a 2 h recording costs a few hundred KB, not ~460 MB of float samples.
/// Speed uses pitch-preserving WSOLA (<see cref="WsolaTimeStretcher"/>) and changes live.
/// </para>
/// <para>
/// Position = the recording sample behind the frame the device reports as played, mapped back
/// through the time-stretcher. Pause and seek stop the device (dropping its buffer) and start
/// it again at the exact position, which keeps the mapping simple and the same on every
/// platform. Public members are thread-safe; events fire on a timer or caller thread.
/// </para>
/// </summary>
internal sealed class PcmAudioPlayer : IAudioPlayer
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly Func<IPcmOutput> _createOutput;
    private RecordingAudioStream? _stream;
    private PlaybackRenderer? _renderer;
    private IPcmOutput? _output;
    private Timer? _timer;
    private bool _playing;
    private bool _disposed;
    private TimeSpan _stoppedPosition;
    private double _speed = 1.0;

    public PcmAudioPlayer(Func<IPcmOutput> createOutput) =>
        _createOutput = createOutput ?? throw new ArgumentNullException(nameof(createOutput));

    public event Action<TimeSpan>? PositionChanged;
    public event Action<bool>? PlayingChanged;
    public event Action? Ended;

    public TimeSpan Duration
    {
        get { lock (_gate) return _renderer?.Duration ?? TimeSpan.Zero; }
    }

    public TimeSpan Position
    {
        get { lock (_gate) return CurrentPosition(); }
    }

    public bool IsPlaying
    {
        get { lock (_gate) return _playing; }
    }

    public double Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            lock (_gate)
            {
                _speed = double.IsFinite(value) ? Math.Clamp(value, PlaybackCursor.MinSpeed, PlaybackCursor.MaxSpeed) : 1.0;
                if (_renderer is not null) _renderer.Speed = _speed;
            }
        }
    }

    public async Task LoadAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        // Opening scans the Ogg page headers (~43 MB for 2 h at 48 kbps): off the UI thread.
        var stream = await Task.Run(() => RecordingAudioStream.Open(audioPath), cancellationToken).ConfigureAwait(false);
        bool wasPlaying;
        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                stream.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(PcmAudioPlayer));
            }
            wasPlaying = StopDevice();
            _stream?.Dispose();
            _stream = stream;
            _renderer = new PlaybackRenderer(new StreamSource(stream)) { Speed = _speed };
            _stoppedPosition = TimeSpan.Zero;
        }
        if (wasPlaying) PlayingChanged?.Invoke(false);
        PositionChanged?.Invoke(TimeSpan.Zero);
    }

    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_renderer is null || _playing) return;
            var duration = _renderer.Duration;
            if (duration <= TimeSpan.Zero) return;
            if (_stoppedPosition >= duration) _stoppedPosition = TimeSpan.Zero; // replay from the start after the end
            StartDevice(_stoppedPosition);
        }
        PlayingChanged?.Invoke(true);
    }

    public void Pause()
    {
        TimeSpan position;
        lock (_gate)
        {
            if (!_playing) return;
            position = CurrentPosition();
            StopDevice();
            _stoppedPosition = position;
        }
        PlayingChanged?.Invoke(false);
        PositionChanged?.Invoke(position);
    }

    public void Seek(TimeSpan position)
    {
        bool stoppedAtEnd = false;
        lock (_gate)
        {
            if (_renderer is null) return;
            var duration = _renderer.Duration;
            position = PlaybackCursor.Clamp(position, duration);
            if (_playing)
            {
                StopDevice();
                if (position < duration) StartDevice(position);
                else stoppedAtEnd = true;
            }
            _stoppedPosition = position;
        }
        if (stoppedAtEnd) PlayingChanged?.Invoke(false);
        PositionChanged?.Invoke(position);
    }

    public ValueTask DisposeAsync()
    {
        IPcmOutput? output;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            StopDevice();
            output = _output;
            _output = null;
            _stream?.Dispose();
            _stream = null;
            _renderer = null;
        }
        output?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Requires the lock.</summary>
    private TimeSpan CurrentPosition()
    {
        if (_renderer is null) return TimeSpan.Zero;
        if (!_playing || _output is null) return _stoppedPosition;
        return _renderer.ToTime(_renderer.SourcePositionAt(_output.PlayedFrames));
    }

    /// <summary>Requires the lock.</summary>
    private void StartDevice(TimeSpan position)
    {
        var renderer = _renderer!;
        renderer.Restart(renderer.ToSamples(position));
        renderer.Speed = _speed;
        if (_output is null)
        {
            _output = _createOutput();
            _output.Failed += OnOutputFailed;
        }
        _output.Start(renderer.SampleRate, renderer.Render);
        _playing = true;
        _stoppedPosition = position;
        _timer ??= new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    /// <summary>Requires the lock. Returns whether it was playing.</summary>
    private bool StopDevice()
    {
        _timer?.Dispose();
        _timer = null;
        if (!_playing) return false;
        _playing = false;
        _output?.Stop();
        return true;
    }

    private void Tick()
    {
        if (!Monitor.TryEnter(_gate)) return; // a control call is running; next tick
        TimeSpan position;
        bool ended = false;
        try
        {
            if (!_playing || _renderer is null || _output is null) return;
            if (_renderer.SourceEnded && _output.PlayedFrames >= _renderer.RenderedFrames)
            {
                StopDevice();
                _stoppedPosition = _renderer.Duration;
                position = _stoppedPosition;
                ended = true;
            }
            else
            {
                position = CurrentPosition();
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }

        PositionChanged?.Invoke(position);
        if (ended)
        {
            PlayingChanged?.Invoke(false);
            Ended?.Invoke();
        }
    }

    private void OnOutputFailed(Exception error)
    {
        System.Diagnostics.Debug.WriteLine($"[playback] Output failed: {error.Message}");
        // Off the device thread: Pause stops (joins) the device.
        _ = Task.Run(Pause);
    }

    /// <summary>Adapts the SpeechLib reader to the Core renderer.</summary>
    private sealed class StreamSource(RecordingAudioStream stream) : IPlaybackSource
    {
        public int SampleRate => stream.SampleRate;
        public long Length => stream.Length;
        public void Seek(long sample) => stream.Seek(sample);
        public int Read(Span<float> buffer) => stream.Read(buffer);
    }
}
