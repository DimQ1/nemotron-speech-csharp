using SpeechLib;
using SpeechLib.Models;

namespace SpeechLib.Audio;

/// <summary>Creates live capture sources backed by the stable NAudio 2 provider.</summary>
public sealed class NAudio2AudioSourceFactory : IAudioSourceFactory
{
    public IAudioSource Create(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic or CaptureMode.Loopback or CaptureMode.Mix =>
            new BufferedCaptureSource(mode, sampleRate),
        _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source.")
    };
}