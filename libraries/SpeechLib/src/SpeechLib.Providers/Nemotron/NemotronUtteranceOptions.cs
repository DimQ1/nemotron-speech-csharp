namespace SpeechLib.Nemotron;

/// <summary>Options of <see cref="NemotronUtteranceRecognizer"/> beyond the engine's own.</summary>
public sealed record NemotronUtteranceOptions
{
    /// <summary>Default of <see cref="StopHistoryEouSeconds"/>: 13 frames of 80 ms.</summary>
    public const double DefaultStopHistoryEouSeconds = 1.04;

    /// <summary>Default of <see cref="MaxUtteranceSeconds"/>, the cap of the apps' plain-recognizer path.</summary>
    public const double DefaultMaxUtteranceSeconds = 30;

    /// <summary>
    /// Silence that closes an utterance when no language tag did: the run of frames without any token, measured on the
    /// stream timeline from the last <b>letter</b> token (punctuation, tags and the bare word marker do not count; chunks
    /// the silence gate dropped are silence). Rounded to whole 80 ms frames. See <see cref="NemotronUtteranceRecognizer"/>
    /// for the measurement behind the default.
    /// </summary>
    public double StopHistoryEouSeconds { get; init; } = DefaultStopHistoryEouSeconds;

    /// <summary>
    /// Longest utterance in seconds (first to last letter token). A longer one is closed in front of the next word;
    /// words are never split. 0 or less disables the cap.
    /// </summary>
    public double MaxUtteranceSeconds { get; init; } = DefaultMaxUtteranceSeconds;

    /// <summary>
    /// A language tag (<c>&lt;en-US&gt;</c>) after the terminal punctuation closes the utterance on the spot.
    /// The model emits it when the next speech begins or a pause has lasted a while, so it also splits sentences that
    /// follow each other without a silence of <see cref="StopHistoryEouSeconds"/>.
    /// </summary>
    public bool EndOnLanguageTag { get; init; } = true;

    /// <summary>
    /// Keep the closing language tag in <see cref="StreamingResult.Final"/> (<c>"Hello world. &lt;en-US&gt;"</c>), as the plain
    /// recognizer's text has it. Consumers strip tags themselves and read the language from them
    /// (<c>OfflineTranscriber.DetectedLanguage</c>, <c>LiveSegmentTimer.DetectedLanguage</c>); the words never contain a
    /// tag. Partial and Stable text never contain tags. False gives a tag-free Final.
    /// </summary>
    public bool KeepLanguageTagInFinal { get; init; } = true;

    /// <summary>
    /// Apply <see cref="SpeechLib.Audio.AutomaticGainControl"/> to the input before the model and the word timer see it.
    /// The model has no input normalization and returns nothing for a quiet microphone (silent at a -40 dBFS peak);
    /// the gain only raises quiet speech, so normally leveled audio passes unchanged, and it keeps the number of samples,
    /// so the stream clock is not affected. The level the gain has learned survives
    /// <see cref="NemotronUtteranceRecognizer.ResetStreamingState"/>: it describes the microphone, not the stream, and
    /// the decorator chain this replaces behaved the same. Off by default (raw input, the parity tests);
    /// <c>RecognizerFactory</c> turns it on.
    /// </summary>
    public bool AutoGain { get; init; }

    /// <summary>Calibration and snapping of the word times.</summary>
    public NemotronWordTimingOptions WordTiming { get; init; } = NemotronWordTimingOptions.Default;
}
