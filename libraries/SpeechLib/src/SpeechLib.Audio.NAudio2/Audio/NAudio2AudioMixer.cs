namespace SpeechLib.Audio;

/// <summary>NAudio 2 mixer: exposes mic/loopback gain and the shared level meter.</summary>
public sealed class NAudio2AudioMixer : IAudioMixer
{
    /// <summary>Singleton instance for the NAudio 2 provider.</summary>
    public static NAudio2AudioMixer Instance { get; } = new();

    private NAudio2AudioMixer() { }

    public float MicVolume
    {
        get => BufferedCaptureSource.MicVolume;
        set => BufferedCaptureSource.MicVolume = value;
    }

    public float LoopbackVolume
    {
        get => BufferedCaptureSource.LoopbackVolume;
        set => BufferedCaptureSource.LoopbackVolume = value;
    }

    public AudioLevelMeter LevelMeter => BufferedCaptureSource.AudioLevelMeter;

    public AudioLevelMeter MicLevelMeter => BufferedCaptureSource.MicLevelMeter;

    public AudioLevelMeter LoopbackLevelMeter => BufferedCaptureSource.LoopbackLevelMeter;
}
