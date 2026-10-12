using SpeechLib.AudioStorage;

namespace VoiceType.Uno.Services.Playback;

/// <summary>
/// Picks the platform player at runtime, like the capture factory selection in App.xaml.cs:
/// Android → AudioTrack; Windows (WinUI head, Skia desktop on Windows) → WASAPI via NAudio 3;
/// Linux → PulseAudio; anything else → <see cref="NullAudioPlayer"/>.
/// </summary>
public sealed class AudioPlayerFactory : IAudioPlayerFactory
{
    public IAudioPlayer Create()
    {
#if __ANDROID__
        return new PcmAudioPlayer(() => new AudioTrackPcmOutput());
#else
        if (OperatingSystem.IsWindows())
            return new PcmAudioPlayer(() => new WasapiPcmOutput());
        if (OperatingSystem.IsLinux())
            return new PcmAudioPlayer(() => new PulsePcmOutput());
        return new NullAudioPlayer();
#endif
    }
}

/// <summary>A player for platforms without an output: knows the duration, never plays.</summary>
public sealed class NullAudioPlayer : IAudioPlayer
{
    private TimeSpan _position;

    public event Action<TimeSpan>? PositionChanged;
    public event Action<bool>? PlayingChanged { add { } remove { } }
    public event Action? Ended { add { } remove { } }

    public TimeSpan Duration { get; private set; }
    public TimeSpan Position => _position;
    public bool IsPlaying => false;
    public double Speed { get; set; } = 1.0;

    public async Task LoadAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        Duration = await Task.Run(() =>
        {
            using var stream = RecordingAudioStream.Open(audioPath);
            return stream.Duration;
        }, cancellationToken).ConfigureAwait(false);
        _position = TimeSpan.Zero;
    }

    public void Play() { }

    public void Pause() { }

    public void Seek(TimeSpan position)
    {
        _position = PlaybackCursor.Clamp(position, Duration);
        PositionChanged?.Invoke(_position);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
