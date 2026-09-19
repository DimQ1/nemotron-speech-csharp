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

    /// <summary>
    /// Number of non-blank tokens produced by the most recent <see cref="ProcessAudio"/> or <see cref="Flush"/> call.
    /// Returns 0 if the implementation does not track token counts.
    /// </summary>
    int LastTokenCount => 0;

    /// <summary>
    /// Provisional text for the audio received so far, or null when the
    /// implementation does not separate partial from final text.
    /// It is revisable: callers should show it after the committed text and
    /// replace it on the next call, never append it to the transcript. Only
    /// <see cref="ProcessAudio"/> deltas and <see cref="Flush"/> output are final.
    /// </summary>
    string? PartialText => null;

    /// <summary>
    /// Reset the recognizer's streaming decode state (decoder state and audio
    /// buffer) so a new utterance starts fresh. Default is a no-op for
    /// recognizers that do not buffer audio between <see cref="ProcessAudio"/>
    /// calls (e.g. streaming GenAI decoders). Buffer-based recognizers
    /// (e.g. Parakeet TDT) override this.
    /// </summary>
    void ResetStreamingState() { }
}
