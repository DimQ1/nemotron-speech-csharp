namespace SpeechLib;

/// <summary>
/// A streaming recognizer that segments utterances from blank-token runs and
/// reports partial (uncommitted) text separately from final (committed) text.
///
/// Blank-based endpointing removes the need for an external VAD model: the
/// transducer decoder emits a token only on non-blank frames, so the silence
/// between consecutive tokens is measured directly from the decoder and an
/// utterance boundary is raised when that silence exceeds
/// <see cref="StopHistoryEouSeconds"/>.
/// </summary>
public interface IUtteranceStreamingRecognizer : IStreamingSpeechRecognizer
{
    /// <summary>Silence (seconds of consecutive blank frames) that closes an utterance.</summary>
    double StopHistoryEouSeconds { get; }

    /// <summary>
    /// Feed an audio chunk and return the current partial text plus any text
    /// finalized at an end-of-utterance boundary crossed while processing it.
    /// </summary>
    StreamingResult ProcessUtterance(float[] chunk);

    /// <summary>Flush remaining audio and finalize the trailing partial text.</summary>
    StreamingResult FlushUtterance();
}
