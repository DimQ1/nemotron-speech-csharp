namespace SpeechLib.Models;

/// <summary>Audio capture mode.</summary>
public enum CaptureMode
{
    /// <summary>Pre-recorded audio file.</summary>
    File,

    /// <summary>Microphone input.</summary>
    Mic,

    /// <summary>System audio loopback.</summary>
    Loopback,

    /// <summary>Microphone + system audio mixed.</summary>
    Mix,
}
