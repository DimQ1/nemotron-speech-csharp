namespace VoiceType.Uno.Services.Playback;

/// <summary>Fills the buffer with mono float audio (silence after the end); returns the count of real frames.</summary>
internal delegate int PcmRender(Span<float> buffer);

/// <summary>
/// A platform output device that plays mono float audio at a fixed rate, pulling it from a
/// <see cref="PcmRender"/> callback on its own thread. <see cref="PcmAudioPlayer"/> drives it
/// with full stop/start cycles (pause, seek), so implementations only need to start, drop
/// everything on stop, and count the frames that reached the speaker.
/// </summary>
internal interface IPcmOutput : IDisposable
{
    /// <summary>Raised on the device thread when playback fails (device lost, server gone).</summary>
    event Action<Exception>? Failed;

    /// <summary>Opens the device and starts pulling from <paramref name="render"/>.</summary>
    void Start(int sampleRate, PcmRender render);

    /// <summary>Stops at once and drops buffered audio; returns after the last <c>render</c> call. No-op when stopped.</summary>
    void Stop();

    /// <summary>Frames the device has played since <see cref="Start"/> (at the speaker, as far as the API tells).</summary>
    long PlayedFrames { get; }
}
