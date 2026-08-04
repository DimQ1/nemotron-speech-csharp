namespace SpeechLib.Audio;

/// <summary>NAudio 3 mixer: exposes mic/loopback gain and the shared level meter.</summary>
public sealed class NAudio3AudioMixer : IAudioMixer
{
    /// <summary>Singleton instance for the NAudio 3 provider.</summary>
    public static NAudio3AudioMixer Instance { get; } = new();

    private NAudio3AudioMixer() { }

    public float MicVolume
    {
        get => NAudio3AudioSource.MicVolume;
        set => NAudio3AudioSource.MicVolume = value;
    }

    public float LoopbackVolume
    {
        get => NAudio3AudioSource.LoopbackVolume;
        set => NAudio3AudioSource.LoopbackVolume = value;
    }

    public AudioLevelMeter LevelMeter => NAudio3AudioSource.AudioLevelMeter;
}
