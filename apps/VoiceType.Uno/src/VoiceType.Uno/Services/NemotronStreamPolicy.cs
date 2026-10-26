using SpeechLib.Nemotron;

namespace VoiceType.Uno.Services;

/// <summary>
/// The stream policies the app runs the Nemotron recognizer with, in one place for the live session
/// (<see cref="RecognitionService"/>, its Stop path flushes the recognizer) and the re-recognition job
/// (<see cref="Jobs.OfflineRecognitionJob"/>, <c>OfflineTranscriber</c> flushes at the end of the file).
/// </summary>
/// <remarks>
/// Measurements in <c>docs/research/nemotron-pure-ort-followups.md</c> (items A1 and A2). A value other than the GenAI default is
/// written into the History settings of a re-recognized version (<c>pauseState</c>, <c>flushClosingChunks</c>), so its text can
/// be explained.
/// </remarks>
internal static class NemotronStreamPolicy
{
    /// <summary>
    /// What the model state does when speech resumes after a pause: the dual branch (3 decision chunks, fresh lead 2) for English
    /// and automatic language, where a new sentence after a pause was often lost with the kept state (EN words after the pause
    /// 28.3 -> 23.7 % WER); Russian keeps the state, its results moved within +-0.6 points. The first words after a pause appear
    /// 1.1 s (560 ms model) or 2.2 s (1120 ms model) later.
    /// </summary>
    public static NemotronPauseStateOptions PauseState { get; } = new()
    {
        Policy = NemotronPauseState.DualBranch,
        KeepLanguages = ["ru"],
    };

    /// <summary>
    /// Chunks of zeros encoded after the padded remainder at Stop and at the end of a file (0 = GenAI's single padded chunk): one
    /// gives back a last word cut by Stop ("medi" -> "medium"; no file got worse), at one encoder pass.
    /// </summary>
    public const int FlushClosingChunks = 1;
}
