using SpeechLib.Audio;

namespace SpeechLib;

public static partial class Transcriber
{
    private static partial IAudioSourceFactory CreateDefaultAudioSourceFactory() =>
        new Audio.NAudio2AudioSourceFactory();
}