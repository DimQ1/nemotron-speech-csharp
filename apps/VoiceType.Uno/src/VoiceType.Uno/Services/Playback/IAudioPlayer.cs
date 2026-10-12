namespace VoiceType.Uno.Services.Playback;

/// <summary>Plays a stored recording (Ogg Opus or WAV). Implementations raise events on any thread.</summary>
public interface IAudioPlayer : IAsyncDisposable
{
    event Action<TimeSpan>? PositionChanged;   // ~every 50–100 ms while playing
    event Action<bool>? PlayingChanged;
    event Action? Ended;

    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    bool IsPlaying { get; }
    double Speed { get; set; }                  // 0.5 .. 2.0

    Task LoadAsync(string audioPath, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Seek(TimeSpan position);
}

public interface IAudioPlayerFactory
{
    IAudioPlayer Create();
}
