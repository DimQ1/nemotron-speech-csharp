namespace SpeechLib.Audio;

/// <summary>
/// Streaming voice-activity detector abstraction. Feeds raw 16 kHz mono audio
/// and reports whether speech is present, so recognizers can skip silence.
/// </summary>
public interface IVadFilter : IDisposable
{
    /// <summary>Speech probability of the most recently scored window (0..1).</summary>
    float LastProbability { get; }

    /// <summary>
    /// Feed an audio batch and return true when any window contains speech.
    /// </summary>
    bool HasSpeech(ReadOnlySpan<float> samples);

    /// <summary>Reset recurrent state and buffered samples (start of a new utterance).</summary>
    void Reset();
}
