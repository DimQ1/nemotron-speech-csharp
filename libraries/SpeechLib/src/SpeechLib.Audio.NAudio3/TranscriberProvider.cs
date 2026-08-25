using SpeechLib.Audio;

namespace SpeechLib;

public static partial class Transcriber
{
    // NAudio3AudioSourceFactory is Windows-only ([SupportedOSPlatform("windows")]).
    // Guard with OperatingSystem.IsWindows() so the CA1416 analyzer is satisfied when
    // this provider is linked into the multi-platform heads (desktop/android).
    private static partial IAudioSourceFactory CreateDefaultAudioSourceFactory()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The NAudio 3 capture provider is only supported on Windows.");

        return new Audio.NAudio3AudioSourceFactory();
    }
}