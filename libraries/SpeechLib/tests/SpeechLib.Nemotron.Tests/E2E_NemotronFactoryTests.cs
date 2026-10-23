using System.Text;
using System.Text.RegularExpressions;
using SpeechLib.Audio;
using SpeechLib.Providers;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The product chain on the real model: what <see cref="RecognizerFactory"/> builds for a Nemotron folder, fed like the apps feed
/// it, against the golden-verified engine (<see cref="NemotronRnntRecognizer"/>, token-exact with the ONNX Runtime GenAI oracle
/// the goldens were recorded with) behind a gain control of its own. The text must be identical, also for quiet microphones
/// (gain control) and across long pauses (silence drop).
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronFactoryTests
{
    private readonly ITestOutputHelper output;

    // The tests build whole recognizer chains of their own, some two or three at a time: give the cached models back first.
    public E2E_NemotronFactoryTests(NemotronModelCache models, ITestOutputHelper output)
    {
        this.output = output;
        models.ReleaseAll();
    }

    private static readonly Regex Tag = new(@"<\w{2,3}(-\w{2,4})?>", RegexOptions.Compiled);

    private static string Normalize(string text) => Regex.Replace(Tag.Replace(text, " "), @"\s+", " ").Trim();

    private static RecognizerFactoryOptions Options(string? language = "en", bool useVad = false) => new()
    {
        ModelPath = NemotronTestEnvironment.LocateModel("int4-c056")!,
        ExecutionProvider = "cpu",
        Language = language,
        UseVad = useVad,
    };

    /// <summary>The plain path as the CLI uses it: one chunk per call, then a flush.</summary>
    private static string RunPlain(IStreamingSpeechRecognizer recognizer, float[] audio)
    {
        recognizer.ResetStreamingState();
        var text = new StringBuilder();
        foreach (var piece in TestAudio.Split(audio, recognizer.ChunkSamples))
            text.Append(recognizer.ProcessAudio(piece));
        text.Append(recognizer.Flush());
        return text.ToString();
    }

    /// <summary>
    /// The plain path rebuilt from its parts: the engine behind a <see cref="AutomaticGainControl"/> of its own, like the chain of
    /// the ONNX Runtime GenAI recognizer this one replaced (gain on every piece, none on the flush).
    /// </summary>
    private static string RunPlainBehindGain(IStreamingSpeechRecognizer engine, float[] audio)
    {
        var gain = new AutomaticGainControl(engine.SampleRate);
        engine.ResetStreamingState();
        var text = new StringBuilder();
        foreach (var piece in TestAudio.Split(audio, engine.ChunkSamples))
            text.Append(engine.ProcessAudio(gain.Process(piece)));
        text.Append(engine.Flush());
        return text.ToString();
    }

    /// <summary>The utterance path as the apps use it: Finals joined with the continuation flag, the open remainder after the flush.</summary>
    private static string RunUtterances(IUtteranceStreamingRecognizer recognizer, float[] audio)
    {
        recognizer.ResetStreamingState();
        var text = "";
        foreach (var piece in TestAudio.Split(audio, recognizer.ChunkSamples))
        {
            var result = recognizer.ProcessUtterance(piece);
            text = UtteranceText.Join(text, result.Final, result.FinalContinuesPrevious);
        }
        var tail = recognizer.FlushUtterance();
        return UtteranceText.Join(text, tail.Final, tail.FinalContinuesPrevious);
    }

    // A quiet room (sigma 12 LSB, about -69 dBFS: below the noise gate of the gain control) and the noisy one of the S0 fixtures (66 LSB,
    // about -54 dBFS), which the gain control takes for speech and pumps up by 20x in every pause.
    private static float[] RoomTone(double seconds, uint seed, float sigmaLsb = 12f) =>
        SyntheticAudio.RoomTone((int)(seconds * 16000), sigmaLsb / 32768f, seed);

    /// <summary>The test clips: a normal one, one that is quiet in itself, a quiet microphone, long pauses between clips, Russian.</summary>
    private static float[] Clip(string name)
    {
        float[] Load(string relative) => NemotronTestEnvironment.LoadAudioRelative(relative);
        switch (name)
        {
            case "en-0001":
                return Load("cv17/en/0001.wav");
            case "en-0024-at-minus-55-dBFS":
                return Load("cv17/en/0024.wav");
            case "en-0001-at-minus-40-dBFS":
                var clip = Load("cv17/en/0001.wav");
                var scale = 0.01f / clip.Max(Math.Abs);
                return clip.Select(s => s * scale).ToArray();
            case "en-pauses":
                return SyntheticAudio.Concat([Load("cv17/en/0001.wav"), RoomTone(4, 1), Load("cv17/en/0002.wav"), RoomTone(2, 2), Load("cv17/en/0003.wav")]);
            case "en-pauses-in-a-noisy-room":
                return SyntheticAudio.Concat([Load("cv17/en/0001.wav"), RoomTone(4, 1, 66f), Load("cv17/en/0002.wav"), RoomTone(2, 2, 66f), Load("cv17/en/0003.wav")]);
            case "en-long-pause":
                return SyntheticAudio.Concat([Load("cv17/en/0001.wav"), RoomTone(8, 3), Load("cv17/en/0002.wav")]);
            case "ru-0001":
                return Load("cv17/ru/0001.wav");
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    // ── what the factory builds ─────────────────────────────────────────

    [NemotronModelFact]
    public void Create_ShouldReturnTheUtteranceRecognizerItselfWithEveryInterfaceTheAppsLookFor()
    {
        using var recognizer = RecognizerFactory.Create(Options());

        var nemotron = Assert.IsType<NemotronUtteranceRecognizer>(recognizer);       // no decorator in between
        Assert.IsAssignableFrom<ITimedUtteranceRecognizer>(recognizer);
        Assert.IsAssignableFrom<IUtteranceStreamingRecognizer>(recognizer);
        Assert.IsAssignableFrom<ILanguageConfigurable>(recognizer);
        Assert.IsAssignableFrom<IRuntimeConfigurable>(recognizer);
        Assert.True(nemotron.Options.AutoGain);
        Assert.Equal(1.04, nemotron.StopHistoryEouSeconds);
        Assert.Equal(8960, recognizer.ChunkSamples);
        Assert.Equal(16000, recognizer.SampleRate);
        Assert.Null(recognizer.PartialText);
    }

    [NemotronModelTheory]
    [InlineData("en", false, 0, "false")]
    [InlineData("ru", true, 11, "true")]
    [InlineData("auto", false, 101, "false")]
    [InlineData(null, true, 0, "true")]
    public void Create_ShouldMapLanguageAndSkipSilenceToTheRecognizer(string? language, bool useVad, int languageId, string vadStatus)
    {
        using var recognizer = RecognizerFactory.Create(Options(language, useVad));

        var nemotron = Assert.IsType<NemotronUtteranceRecognizer>(recognizer);
        Assert.Equal(languageId, nemotron.Engine.LanguageId);
        Assert.Equal(vadStatus, nemotron.VadStatus);

        // the apps apply both settings again at every start
        Assert.True(((IRuntimeConfigurable)recognizer).TrySetVad(!useVad));
        Assert.Equal(useVad ? "false" : "true", nemotron.VadStatus);
        Assert.True(((ILanguageConfigurable)recognizer).TrySetLanguage("ru"));
        Assert.Equal(11, nemotron.Engine.LanguageId);
        Assert.True(((IRuntimeConfigurable)recognizer).TrySetSearchOptions(1, 1.1));
    }

    [NemotronModelFact]
    public void Create_ShouldKeepTheLanguageTagInFinalUnlessAskedNotTo()
    {
        using var keeps = RecognizerFactory.Create(Options());
        using var typing = RecognizerFactory.Create(Options() with { KeepLanguageTagInFinal = false });

        Assert.True(Assert.IsType<NemotronUtteranceRecognizer>(keeps).Options.KeepLanguageTagInFinal);       // History reads the language from it
        Assert.False(Assert.IsType<NemotronUtteranceRecognizer>(typing).Options.KeepLanguageTagInFinal);    // text that is typed must only grow
    }

    // ── the product chain gives the text of the engine behind a gain control ─────

    [NemotronModelTheory]
    [InlineData("en-0001", "en", false)]
    [InlineData("en-0024-at-minus-55-dBFS", "en", false)]
    [InlineData("en-0001-at-minus-40-dBFS", "en", false)]
    [InlineData("en-pauses", "en", false)]
    [InlineData("en-pauses-in-a-noisy-room", "en", false)]      // the gain pumps in the pauses: equal to the reference by construction, a guard against drift only
    [InlineData("en-long-pause", "en", true)]
    [InlineData("ru-0001", "ru", false)]
    public void TheDefaultChain_ShouldGiveTheTextOfTheEngineBehindAGainControl(string clipName, string language, bool useVad)
    {
        var audio = Clip(clipName);
        string reference;
        using (var engine = new NemotronRnntRecognizer(Options().ModelPath, "cpu", LanguageMapper.Resolve(language), useVad))
            reference = RunPlainBehindGain(engine, audio);

        string plain;
        using (var chain = RecognizerFactory.Create(Options(language, useVad)))
            plain = RunPlain(chain, audio);

        // a recognizer of its own for the second run: the gain control keeps what it learned about the microphone, so the same
        // recognizer would start the clip with another gain than the reference did
        string utterances;
        using (var chain = RecognizerFactory.Create(Options(language, useVad)))
            utterances = RunUtterances((IUtteranceStreamingRecognizer)chain, audio);

        output.WriteLine($"{clipName}: \"{Normalize(reference)}\"");
        Assert.Equal(reference, plain);                                                // the plain path: the very same string
        Assert.Equal(Normalize(reference), Normalize(utterances));                     // the utterance path: the same words
    }

    [NemotronModelFact]
    public void AQuietMicrophone_ShouldStillTranscribeThroughTheDefaultChain()
    {
        var quiet = Clip("en-0001-at-minus-40-dBFS");
        var normal = Clip("en-0001");
        using var chain = RecognizerFactory.Create(Options());
        using var raw = new NemotronRnntRecognizer(Options().ModelPath, "cpu", "0", useVad: false);

        var heard = Normalize(RunPlain(chain, quiet));
        var reference = Normalize(RunPlain(chain, normal));
        var withoutGain = Normalize(RunPlain(raw, quiet));

        output.WriteLine($"normal level: \"{reference}\"");
        output.WriteLine($"-40 dBFS with gain control: \"{heard}\"");
        output.WriteLine($"-40 dBFS raw model: \"{withoutGain}\"");
        Assert.NotEmpty(heard);
        // the gain control gives the sentence back whole: measured, the very same words but for one the model hears differently
        var heardWords = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var referenceWords = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(referenceWords.Length, heardWords.Length);
        Assert.True(heardWords.Zip(referenceWords).Count(pair => pair.First != pair.Second) <= 1, "a -40 dBFS microphone should give the normal level's sentence back");
        Assert.True(withoutGain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < heard.Split(' ').Length, "without gain control the quiet clip should come out worse");
    }
}
