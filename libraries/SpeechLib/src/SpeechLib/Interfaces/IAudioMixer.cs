namespace SpeechLib.Audio;

/// <summary>
/// Provider-independent control over the live capture mixer:
/// per-source gain (mic/loopback) and the current input audio level.
/// </summary>
public interface IAudioMixer
{
    /// <summary>Mic gain (0.0 – 1.0). Applied in real time during mixing.</summary>
    float MicVolume { get; set; }

    /// <summary>Loopback gain (0.0 – 1.0). Applied in real time during mixing.</summary>
    float LoopbackVolume { get; set; }

    /// <summary>Shared level meter publishing the current input level (RMS, 0–1).</summary>
    AudioLevelMeter LevelMeter { get; }
}
