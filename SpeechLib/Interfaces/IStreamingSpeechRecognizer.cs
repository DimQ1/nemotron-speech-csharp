namespace SpeechLib;

/// <summary>
/// Streaming speech recognition engine abstraction.
/// Implementations feed audio chunks incrementally and return decoded text as it becomes available.
/// </summary>
public interface IStreamingSpeechRecognizer : IDisposable
{
    /// <summary>Sample rate the recognizer expects (Hz).</summary>
    int SampleRate { get; }

    /// <summary>Number of samples per processing chunk (model-dependent).</summary>
    int ChunkSamples { get; }

    /// <summary>
    /// Feed an audio chunk to the recognizer.
    /// Returns new transcription text produced since the last call (may be empty).
    /// Returns null if the processor has not accumulated enough audio yet.
    /// </summary>
    string? ProcessAudio(float[] chunk);

    /// <summary>
    /// Flush remaining audio and return any final transcription text.
    /// Call once at the end of the audio stream.
    /// </summary>
    string? Flush();
}
