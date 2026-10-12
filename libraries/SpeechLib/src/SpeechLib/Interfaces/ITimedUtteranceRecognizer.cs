using SpeechLib.Models;

namespace SpeechLib;

/// <summary>
/// An utterance recognizer that also reports exact token / word timings from its decoder
/// (<see cref="StreamingResult.FinalWords"/> / <see cref="StreamingResult.PartialWords"/>).
/// Times are seconds on the absolute timeline of the samples fed since the last
/// <see cref="IStreamingSpeechRecognizer.ResetStreamingState"/>, independent of chunking,
/// context windows and skipped silence.
/// </summary>
public interface ITimedUtteranceRecognizer : IUtteranceStreamingRecognizer
{
    /// <summary>Transcribe a complete waveform and return its words with timings (independent of the streaming state).</summary>
    IReadOnlyList<WordTiming> TranscribeWords(float[] waveform);
}
