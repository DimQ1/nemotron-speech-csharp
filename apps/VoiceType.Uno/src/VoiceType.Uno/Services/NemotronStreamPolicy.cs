using SpeechLib.Nemotron;

namespace VoiceType.Uno.Services;

/// <summary>
/// The stream policies the app runs the Nemotron recognizer with, in one place for the live session
/// (<see cref="RecognitionService"/>, its Stop path flushes the recognizer) and the re-recognition job
/// (<see cref="Jobs.OfflineRecognitionJob"/>, <c>OfflineTranscriber</c> flushes at the end of the file).
/// </summary>
/// <remarks>
/// Both are still the GenAI-parity defaults: the measurements and the open decision are in
/// <c>docs/research/nemotron-pure-ort-followups.md</c> (items A1 and A2). A value other than the default is written into the
/// History settings of a re-recognized version (<c>pauseState</c>, <c>flushClosingChunks</c>), so its text can be explained.
/// </remarks>
internal static class NemotronStreamPolicy
{
    /// <summary>What the model state does when speech resumes after a pause; null keeps it (GenAI).</summary>
    public static NemotronPauseStateOptions? PauseState => null;

    /// <summary>Chunks of zeros encoded after the padded remainder at Stop and at the end of a file (0 = GenAI's single padded chunk).</summary>
    public const int FlushClosingChunks = 0;
}
