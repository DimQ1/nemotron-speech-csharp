namespace SpeechLib.Nemotron;

/// <summary>What the model state of a stream does when speech resumes after a pause (see <see cref="NemotronPauseStateOptions"/>).</summary>
public enum NemotronPauseState
{
    /// <summary>
    /// The encoder caches and the decoder state run on through every pause, as in ONNX Runtime GenAI: the default, required for
    /// token-for-token parity with the recorded GenAI goldens.
    /// </summary>
    Keep,

    /// <summary>
    /// At the first speech chunk after a pause the stream continues from a fresh model state (primed with the last chunks of the
    /// pause), a plain reset that keeps the stream clock. The closing mark and language tag of the sentence before, which the
    /// kept state writes when the next speech begins, are taken over from the kept state. Measured worse than
    /// <see cref="DualBranch"/> when the speech after the pause continues the sentence; for comparison and for languages where
    /// the measurement says so.
    /// </summary>
    Fresh,

    /// <summary>
    /// At the first speech chunk after a pause the next <see cref="NemotronPauseStateOptions.DecisionSeconds"/> of audio are
    /// decoded twice, by the kept state and by a fresh one; the branch that wrote more letter tokens continues (a tie keeps the
    /// state), the other one is dropped. The text of those chunks is held back until the decision.
    /// </summary>
    DualBranch,
}

/// <summary>
/// The pause-state policy of <see cref="NemotronRnntRecognizer"/>: whether the model keeps the state it had before a pause when
/// the speech resumes, starts afresh, or decides per pause.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> The model state after a pause is wrong in one of two cases. When a NEW sentence starts after the pause, the
/// stale state (24 layers of attention cache over the last 4.5 s and the decoder LSTM) makes the model delete a fifth to a third
/// of the next sentence's words in English (next-utterance WER 41-48 % against 26-29 % with a fresh state, CV17 clips, gaps of
/// 1 to 6 s); when the speech after a hesitation CONTINUES the sentence, the fresh state is the wrong one (2 to 8 points worse).
/// The better branch of the two is nearly always the one that decodes more words, which <see cref="NemotronPauseState.DualBranch"/>
/// uses. Measurements: <c>docs/research/onnxruntime-genai-ideas.md</c> item A1 and <c>docs/research/nemotron-pure-ort-followups.md</c>.</para>
/// <para><b>When a pause counts.</b> The silence gate scores every chunk (the native Silero chunk gate of the silence drop rule, or a
/// gate of the policy's own when the silence drop is off). A chunk with speech that follows at least
/// <see cref="PauseSeconds"/> of chunks without speech, in a stream whose model state has already decoded speech, starts the
/// policy. Chunks the silence drop rule did not encode count as pause like the others.</para>
/// <para><b>The stream clock runs on</b>: a fresh branch is a model-state-only reset, chunk indices, word times and
/// <see cref="NemotronRnntRecognizer.ChunksConsumed"/> continue. The text of a branch decision window is reported in the call that
/// ends the window (<see cref="NemotronRnntRecognizer.LastChunks"/> then lists all its chunks); it never depends on how the caller
/// batched the audio.</para>
/// <para><b>Cost</b> per pause: <see cref="PrerollSeconds"/> plus <see cref="DecisionSeconds"/> of extra encoder work (about 0.3 s of
/// CPU on the 560 ms model at four threads) and, for the first use, a second set of encoder caches (about 13 MB).</para>
/// </remarks>
public sealed record NemotronPauseStateOptions
{
    /// <summary>The GenAI behaviour: the state is kept through every pause.</summary>
    public static NemotronPauseStateOptions Parity { get; } = new();

    /// <summary>What happens at speech resumption after a pause; <see cref="NemotronPauseState.Keep"/> (GenAI parity) by default.</summary>
    public NemotronPauseState Policy { get; init; } = NemotronPauseState.Keep;

    /// <summary>
    /// Silence before speech that counts as a pause, rounded to whole chunks (at least one): 1.1 s is two chunks of the 560 ms
    /// model and one of the 1120 ms model.
    /// </summary>
    public double PauseSeconds { get; init; } = 1.1;

    /// <summary>
    /// Audio decoded by both branches before <see cref="NemotronPauseState.DualBranch"/> decides, counted from the first speech
    /// chunk and rounded to whole chunks (at least one); the text of those chunks is held back until then. The flush of a stream
    /// ends a window early and takes part in it.
    /// </summary>
    public double DecisionSeconds { get; init; } = 1.68;

    /// <summary>
    /// The end of the pause fed to a fresh state in front of the speech, rounded to whole chunks (at most the pause's silent
    /// chunks), so the fresh model hears a normal stream start (room tone, then speech) instead of speech out of nothing.
    /// </summary>
    public double PrerollSeconds { get; init; } = 1.1;

    /// <summary>Converts a duration of this record to whole chunks of <paramref name="chunkSeconds"/>, at least one.</summary>
    internal static int ToChunks(double seconds, double chunkSeconds) =>
        Math.Max(1, (int)Math.Round(seconds / chunkSeconds, MidpointRounding.AwayFromZero));
}

/// <summary>One decision of the pause-state policy (diagnostics, see <see cref="NemotronRnntRecognizer.PauseDecided"/>).</summary>
/// <param name="FirstChunk">The first speech chunk after the pause, on the stream grid.</param>
/// <param name="Chunks">Chunks both branches decoded (the window, flush chunks included).</param>
/// <param name="SilentChunks">Chunks without speech in front of it.</param>
/// <param name="KeepLetters">Letter tokens of the branch that kept the state.</param>
/// <param name="FreshLetters">Letter tokens of the fresh branch.</param>
/// <param name="FreshWon">The fresh branch continues the stream.</param>
/// <param name="KeepText">What the kept state decoded in the window.</param>
/// <param name="FreshText">What the fresh state decoded in the window.</param>
public sealed record NemotronPauseDecision(int FirstChunk, int Chunks, int SilentChunks, int KeepLetters, int FreshLetters, bool FreshWon,
    string KeepText, string FreshText);
