using SpeechLib.Models;

namespace SpeechLib;

/// <summary>Creates live audio sources for a concrete capture provider.</summary>
public interface IAudioSourceFactory
{
    /// <summary>Creates a source for the requested live capture mode.</summary>
    IAudioSource Create(CaptureMode mode, int sampleRate);
}