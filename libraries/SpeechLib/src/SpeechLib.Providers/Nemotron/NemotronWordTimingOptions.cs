namespace SpeechLib.Nemotron;

/// <summary>
/// How far the frame in which the model emitted the first token of a word lags the start of the word, measured on
/// synthetic speech (see <see cref="NemotronLagTable"/>). All values are seconds.
/// </summary>
/// <param name="StartLagSeconds">Median of <c>80 ms * globalFrame - true word start</c>; a word starts this much before its emission frame.</param>
/// <param name="EndLagSeconds">What is subtracted from the END of the last letter token's frame slot to get the word end (the end of the slot is 80 ms after its start, so this is the start lag minus the slot length plus the lag of the word end).</param>
public readonly record struct NemotronLagCalibration(double StartLagSeconds, double EndLagSeconds);

/// <summary>Speech/silence snapping of word starts and ends (see <see cref="NemotronWordTimingOptions.Snap"/>).</summary>
public sealed record NemotronSnapOptions
{
    /// <summary>A speech onset must follow at least this many silent 10 ms frames (200 ms) to count as the start of a word after a pause. 100 ms found phantom pauses in reverberant, noisy audio; 150-250 ms were equally good on clean dialogue.</summary>
    public int MinSilenceFrames { get; init; } = 20;

    /// <summary>How far before the calibrated start a word start may be moved (seconds). Moving earlier is what hurts the speaker assignment, so it is nearly off.</summary>
    public double MaxBackwardSeconds { get; init; } = 0.03;

    /// <summary>How far after the calibrated start a word start may be moved (seconds): after a pause the emission lags less than after continuous speech, so the calibrated start is early.</summary>
    public double MaxForwardSeconds { get; init; } = 0.35;

    /// <summary>How far from the calibrated end the speech offset in front of a pause may be (seconds); for the last word of an utterance only before it, see <see cref="LastEndMaxForwardSeconds"/>.</summary>
    public double EndWindowSeconds { get; init; } = 0.35;

    /// <summary>
    /// How far after the calibrated end of the LAST word of an utterance its end may be moved to a speech offset (seconds).
    /// The next word of the utterance is not known, and speech that the model has not emitted yet follows the last word
    /// without a pause: a pause further away belongs to the words after it (a real clip ended an utterance on a tag, the
    /// next word followed two frames later, and the end of the last word was snapped 0.31 s ahead, onto the end of that
    /// next word). 0.20 s costs 2-3 ms of the median and 9-10 ms of the 90th percentile end error on clean synthetic speech.
    /// </summary>
    public double LastEndMaxForwardSeconds { get; init; } = 0.20;

    /// <summary>
    /// Shortest word, and shortest distance between the starts of two words, after snapping (seconds): one encoder frame, the
    /// resolution of the emission clock. The model shows a word at most once per frame (words emitted together after a pause
    /// are consecutive frames apart), so two words closer than that were pushed onto each other by the snapping, which has
    /// only one speech onset for the whole burst, not heard together. A later start is the harmless direction.
    /// </summary>
    public double MinWordSeconds { get; init; } = 0.08;

    /// <summary>Percentile (0..1) of the recent frame energies taken as the noise floor.</summary>
    public double NoiseFloorPercentile { get; init; } = 0.05;

    /// <summary>A frame is speech when it is this many dB above the noise floor.</summary>
    public double MarginDb { get; init; } = 12;

    /// <summary>Lowest speech threshold in dBFS (very quiet rooms: the threshold never drops below it).</summary>
    public double MinThresholdDb { get; init; } = -60;

    /// <summary>A frame is a candidate for the speech level when it is this many dB above the noise floor (see <see cref="SpeechLevelPercentile"/>).</summary>
    public double SpeechCandidateMarginDb { get; init; } = 8;

    /// <summary>
    /// The speech level (0..1): this percentile of the frames that are at least <see cref="SpeechCandidateMarginDb"/> above the noise
    /// floor. Independent of the share of speech in the history (a dictation has a few percent, a conversation a third); on the
    /// reverberant sets of <see cref="MinSpeechToNoiseDb"/> it equals the 95th percentile of all frames to a few tenths of a dB.
    /// </summary>
    public double SpeechLevelPercentile { get; init; } = 0.9;

    /// <summary>
    /// The speech level (see <see cref="SpeechLevelPercentile"/>) must be at least this many dB above the noise floor,
    /// otherwise speech and noise cannot be told apart and nothing is snapped (the times stay calibrated-only). Measured on
    /// reverberant speech with pink noise: the level difference is 22-25 dB at an SNR of 15 dB (snapping helps: start median
    /// 80 -> 73 ms, early starts 9.4 -> 4.7 %), 18-20 dB at SNR 10 dB (about neutral, worse for Russian) and 13-15 dB at
    /// SNR 5 dB (snapping makes it worse: median 103 -> 141 ms); the value lies between the first two. A level difference
    /// is used, not an absolute floor: how loud the noise is in dBFS depends on the recording level, not on the noise problem.
    /// </summary>
    public double MinSpeechToNoiseDb { get; init; } = 21;

    /// <summary>Seconds of audio energy kept for the noise floor and for the words of one utterance (must exceed the utterance cap).</summary>
    public double HistorySeconds { get; init; } = 60;
}

/// <summary>
/// Parameters that turn the emission frames of the cache-aware transducer into word times. Everything has a
/// measured default (see <see cref="NemotronLagTable"/>); the record exists so that a host can switch the corrections
/// off (<see cref="Calibrate"/>, <see cref="Snap"/> = null) or tune them for its audio.
/// </summary>
/// <remarks>
/// <para>A word starts at the emission frame of its first token minus the calibrated lag (<see cref="NemotronLagTable"/>
/// per chunk size and language, a late-biased default for languages that were not measured), minus nothing further; it
/// ends at the end of the frame slot of its last letter token minus the calibrated end lag, never after the start of the
/// next word. Words that follow a pause are then put on the speech onset found in the audio energy (<see cref="Snap"/>),
/// which removes the early starts that hurt the speaker assignment.</para>
/// <para>These are the one set of times for display, seeking and the speaker assignment. Measured with the app's diarizer on real
/// speech (Common Voice conversations, AMI meetings) the assignment is as accurate as with the raw emission slot (late by 0.2 to
/// 0.4 s), stays flat when the starts are made up to 200 ms later, and loses up to 20 points when they are made 100 ms earlier:
/// late is harmless, early is not, so the calibration is biased late and the snapping moves starts forward (30 ms back at most).</para>
/// </remarks>
public sealed record NemotronWordTimingOptions
{
    /// <summary>Subtract the measured emission lag. False gives the raw frame slot start (late by 0.2 to 0.3 s).</summary>
    public bool Calibrate { get; init; } = true;

    /// <summary>
    /// Pulled towards the late side: added to every calibrated start (seconds). A late start costs nothing for the
    /// speaker assignment, an early one lands in silence and inherits the previous speaker.
    /// </summary>
    public double LateBiasSeconds { get; init; } = 0.015;

    /// <summary>Overrides the measured lag of a language (BCP-47 primary subtag, "en") for every chunk size; null keeps the table.</summary>
    public IReadOnlyDictionary<string, NemotronLagCalibration>? LanguageOverrides { get; init; }

    /// <summary>Lag used when the language is unknown or was not measured; null selects the table's conservative default for the chunk size.</summary>
    public NemotronLagCalibration? UnknownLanguageLag { get; init; }

    /// <summary>Energy snapping of words that follow a pause; null switches it off.</summary>
    public NemotronSnapOptions? Snap { get; init; } = new();

    /// <summary>The defaults.</summary>
    public static NemotronWordTimingOptions Default { get; } = new();

    /// <summary>Raw frame times: no calibration, no snapping.</summary>
    public static NemotronWordTimingOptions Raw { get; } = new() { Calibrate = false, LateBiasSeconds = 0, Snap = null };
}
