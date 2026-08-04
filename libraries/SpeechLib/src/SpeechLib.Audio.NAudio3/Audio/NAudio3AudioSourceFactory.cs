using SpeechLib.Models;
using SpeechLib;

namespace SpeechLib.Audio;

/// <summary>Creates live capture sources backed by the NAudio 3 preview provider.</summary>
public sealed class NAudio3AudioSourceFactory : IAudioSourceFactory
{
    static NAudio3AudioSourceFactory()
    {
        AudioMixerRegistry.Register<NAudio3AudioSourceFactory>(NAudio3AudioMixer.Instance);
    }

    public IAudioSource Create(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic or CaptureMode.Loopback or CaptureMode.Mix =>
            new NAudio3AudioSource(mode, sampleRate),
        _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source.")
    };
}