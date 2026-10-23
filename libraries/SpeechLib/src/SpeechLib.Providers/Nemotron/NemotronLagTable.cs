namespace SpeechLib.Nemotron;

/// <summary>
/// The emission lag of the Nemotron streaming models, measured on synthetic speech (Windows SAPI voices, 4 English and
/// 2 Russian voices, 1 300 words per language and variant): the frame in which the model emits the first token of a word
/// lies <see cref="NemotronLagCalibration.StartLagSeconds"/> after the start of the word (median), with a spread of about
/// 100 ms (standard deviation) that depends on the audio, not on anything the recognizer can see.
/// </summary>
/// <remarks>
/// <para>The lag is a property of the (chunk size, language) pair: the 1120 ms models decide earlier in the audio than the
/// 560 ms ones, Russian words are emitted about 70 to 100 ms later than English ones. Languages that were not measured get
/// a conservative value from the late side: a start that is late costs the speaker assignment nothing, an early one lands in
/// silence.</para>
/// <para>On real speech the lag is larger and more scattered than on this clean synthetic speech. Against Parakeet decoder
/// times (median of the emission slot minus the reference start, 560 ms model): meeting speech (AMI, headsets) English 246 ms
/// (IQR 174 ms) against 224 ms here; Common Voice (crowd-sourced microphones) Russian 292 ms (IQR 132 ms) against 298 ms, English
/// 382 ms (IQR 226 ms). So the displayed starts are about right on good recordings and up to 170 ms late on poor English ones;
/// late costs the speaker assignment nothing (measured with the real diarizer, see <see cref="NemotronWordTimingOptions"/>).</para>
/// <para>The table is data, not logic: <see cref="NemotronWordTimingOptions.LanguageOverrides"/> replaces an entry, and
/// <see cref="NemotronWordTimingOptions.UnknownLanguageLag"/> the default.</para>
/// </remarks>
public static class NemotronLagTable
{
    /// <summary>Samples per chunk of the 560 ms models.</summary>
    public const int Chunk560 = 8960;

    /// <summary>Samples per chunk of the 1120 ms models.</summary>
    public const int Chunk1120 = 17920;

    private static readonly Dictionary<(int Chunk, string Language), NemotronLagCalibration> Measured = new()
    {
        [(Chunk560, "en")] = new(0.224, 0.144),
        [(Chunk560, "ru")] = new(0.298, 0.178),
        [(Chunk1120, "en")] = new(0.182, 0.104),
        [(Chunk1120, "ru")] = new(0.258, 0.142),
    };

    private static readonly NemotronLagCalibration Unknown560 = new(0.150, 0.104);
    private static readonly NemotronLagCalibration Unknown1120 = new(0.110, 0.064);

    /// <summary>The measured entries, for documentation and tests.</summary>
    public static IReadOnlyDictionary<(int Chunk, string Language), NemotronLagCalibration> Entries => Measured;

    /// <summary>The conservative lag for a chunk size and a language without measurement.</summary>
    public static NemotronLagCalibration DefaultFor(int chunkSamples) =>
        chunkSamples >= Chunk1120 ? Unknown1120 : Unknown560;

    /// <summary>
    /// The lag to use for words of <paramref name="language"/> (primary subtag such as "en", or null when unknown) from a model
    /// with chunks of <paramref name="chunkSamples"/> samples, honouring the overrides of <paramref name="options"/>.
    /// </summary>
    public static NemotronLagCalibration Lookup(int chunkSamples, string? language, NemotronWordTimingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (language is not null)
        {
            if (options.LanguageOverrides is { } overrides && overrides.TryGetValue(language, out var overridden))
                return overridden;
            if (Measured.TryGetValue((chunkSamples, language), out var measured))
                return measured;
        }
        return options.UnknownLanguageLag ?? DefaultFor(chunkSamples);
    }
}
