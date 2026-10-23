using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// What the golden oracle cannot cover (it was recorded with fixed languages on a fixed thread count): the automatic language
/// (101), a language change in the middle of a stream, other thread counts and a model folder that fails to load. These tests
/// compare with the goldens' <i>text</i> or, where the stream is unchanged, their token ids; there is no GenAI reference for the
/// switched stream any more, so "sensible text" and a consistent stream state are what is asserted. Skipped without the model
/// and the Common Voice test audio.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronStreamControlTests(NemotronModelCache models, ITestOutputHelper output)
{
    private const string Variant = NemotronTestEnvironment.DefaultVariant;

    // ── automatic language ──────────────────────────────────────────────

    [NemotronModelTheory]
    [InlineData("en0001", "cv17/en/0001.wav")]
    [InlineData("en0002", "cv17/en/0002.wav")]
    [InlineData("en0004", "cv17/en/0004.wav")]
    [InlineData("ru0001", "cv17/ru/0001.wav")]
    [InlineData("ru0002", "cv17/ru/0002.wav")]
    [InlineData("ru0004", "cv17/ru/0004.wav")]
    public void LanguageAuto_ShouldTranscribeLikeTheExplicitLanguage(string clip, string audioPath)
    {
        var golden = GoldenCase.Get($"{Variant}_{clip}");      // recorded with the explicit language (en 0 / ru 11)
        var recognizer = models.Get(Variant);
        Assert.True(recognizer.TrySetLanguage("auto"));
        Assert.Equal(101, recognizer.LanguageId);
        var audio = NemotronTestEnvironment.LoadAudioRelative(audioPath);

        var replay = Replay.Run(recognizer, audio, recognizer.ChunkSamples);

        AssertSensibleStream(recognizer, audio.Length, replay, golden.Text, allowedDistanceShare: 0.25, clip);
    }

    // ── language change in the middle of a stream ───────────────────────

    [NemotronModelFact]
    public void LanguageChangeMidStream_ShouldApplyFromTheNextChunkAndKeepTheStreamConsistent()
    {
        var recognizer = models.Get(Variant);
        var english = GoldenCase.Get($"{Variant}_en0001");
        var russian = GoldenCase.Get($"{Variant}_ru0001");
        var n = recognizer.ChunkSamples;
        var enAudio = english.LoadAudio();
        var ruAudio = russian.LoadAudio();
        var wholeChunks = enAudio.Length / n;                          // the English part ends on a chunk boundary
        enAudio = enAudio[..(wholeChunks * n)];

        // English first (language 0), then the language is switched and Russian carries on in the same stream
        Assert.True(recognizer.TrySetLanguage("en"));
        var replay = new Replay();
        foreach (var piece in TestAudio.Split(enAudio, n))
            Collect(replay, recognizer, piece);
        Assert.Equal(wholeChunks, recognizer.ChunksConsumed);
        Assert.Equal(0, recognizer.BufferedSamples);

        Assert.True(recognizer.TrySetLanguage("ru"));
        Assert.Equal(11, recognizer.LanguageId);
        Assert.Equal(wholeChunks, recognizer.ChunksConsumed);          // the switch changes no counter
        foreach (var piece in TestAudio.Split(ruAudio, n))
            Collect(replay, recognizer, piece);
        replay.CollectFlush(recognizer);

        // chunks encoded before the switch are exactly what an unswitched English stream gives: the change touches nothing earlier
        var before = replay.IdsByChunk().Where(c => c.Key < wholeChunks).ToDictionary(c => c.Key, c => c.Value);
        foreach (var chunk in english.Chunks.Where(c => c.Processed && c.Index >= 0 && c.Index < wholeChunks))
            Assert.True(chunk.Ids.SequenceEqual(before[chunk.Index]), $"chunk {chunk.Index} changed by a later language switch");
        Assert.Equal(wholeChunks, before.Count);

        // the stream goes on without a hiccup: one chunk per slot of the grid, tokens in order inside the audio, nothing left over
        var totalAudio = enAudio.Length + ruAudio.Length;
        var gridChunks = (totalAudio + n - 1) / n;
        Assert.Equal(gridChunks, recognizer.ChunksConsumed);
        Assert.Equal(0, recognizer.BufferedSamples);
        Assert.False(recognizer.IsFaulted);
        ParityAssert.TimelineInvariants(recognizer, replay.Tokens, totalAudio);

        // and the text of both halves is sensible: the English words, then the Russian ones (a carried decoder state may cost a word at the seam)
        var text = replay.Text.ToString();
        var expected = english.Text + " " + russian.Text;
        var distance = WordText.Distance(WordText.Words(WordText.WithoutLanguageTags(expected)), WordText.Words(WordText.WithoutLanguageTags(text)));
        var expectedWords = WordText.Words(WordText.WithoutLanguageTags(expected)).Length;
        output.WriteLine($"switched stream: {text}");
        output.WriteLine($"expected:        {expected}");
        output.WriteLine($"word distance {distance} of {expectedWords}");
        Assert.True(distance <= Math.Max(2, expectedWords * 0.2), $"the switched stream's text is off by {distance} words: {text}");
        Assert.Contains(text.ToCharArray(), ch => ch is >= 'Ѐ' and <= 'ӿ');   // Cyrillic after the switch
        Assert.Contains(text.ToCharArray(), ch => ch is >= 'a' and <= 'z');              // Latin before it
    }

    [NemotronModelFact]
    public void Reset_ShouldKeepTheChosenLanguageAndDecodeLikeAFreshRecognizerOfThatLanguage()
    {
        // ResetStreamingState keeps the language (a setting, not part of the stream); a new stream decodes with the last choice.
        var recognizer = models.Get(Variant);
        var russian = GoldenCase.Get($"{Variant}_ru0001");
        Assert.True(recognizer.TrySetLanguage("ru"));
        recognizer.ResetStreamingState();
        Assert.Equal(11, recognizer.LanguageId);
        var audio = russian.LoadAudio();

        var replay = Replay.Run(recognizer, audio, recognizer.ChunkSamples);

        ParityAssert.MatchesGolden(russian, audio.Length, replay);     // identical to the Russian oracle: nothing of any earlier language remains
    }

    // ── thread counts ───────────────────────────────────────────────────

    [NemotronModelTheory]
    [InlineData(1)]
    [InlineData(12)]
    public void Tokens_ShouldNotDependOnTheIntraOpThreadCount(int threads)
    {
        // The goldens were recorded with 4 threads, the apps use ComputeOptimalIntraThreads (10 on a 20-thread laptop). GenAI's
        // token ids were thread independent (checked for 1, 4 and 12 on 16 clips); the plain engine has to be as well.
        models.ReleaseAll();
        var directory = NemotronTestEnvironment.LocateModel(Variant)!;
        var cases = GoldenCase.Enumerate(vad: false)
            .Where(c => c.Variant == Variant && c.TokenCount > 0 && c.Name.StartsWith($"{Variant}_", StringComparison.Ordinal) && c.Clip is "en0001" or "en0002" or "ru0001" or "ru0002")
            .OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(cases);
        using var recognizer = new NemotronRnntRecognizer(directory, "cpu", null, false, new NemotronRecognizerOptions { CpuThreads = threads });

        foreach (var golden in cases)
        {
            recognizer.ResetStreamingState();
            Assert.True(recognizer.TrySetLanguage(golden.LangId ?? "0"));
            var audio = golden.LoadAudio();

            var replay = Replay.Run(recognizer, audio, recognizer.ChunkSamples);

            var (chunks, tokens) = ParityAssert.MatchesGolden(golden, audio.Length, replay);
            output.WriteLine($"{threads} threads, {golden.Name}: {chunks} chunks, {tokens} tokens identical to the GenAI oracle");
        }
    }

    // ── a model folder that does not fit the config ─────────────────────

    [NemotronModelFact]
    public void Engine_WhenTheGraphsDoNotMatchTheConfig_ShouldFailCleanlyAndTheFolderStillLoadsAfterwards()
    {
        // The step model is the last thing the engine constructor builds, after the sessions and every pinned tensor exist: the
        // failure path has to release all of it (see NemotronRnntEngine). A wrong output name in the config reaches exactly that point.
        models.ReleaseAll();
        var directory = NemotronTestEnvironment.LocateModel(Variant)!;
        var config = NemotronModelConfig.Load(directory);
        var options = new NemotronEngineOptions("cpu", CpuThreads: 2);
        var wrongJoint = config with { Joiner = config.Joiner with { Output = "no_such_output" } };
        var wrongEncoder = config with { Encoder = config.Encoder with { CacheTimeInput = "no_such_cache" } };

        var joint = Assert.Throws<InvalidDataException>(() => NemotronRnntEngine.Create(directory, wrongJoint, options));
        var encoder = Assert.Throws<InvalidDataException>(() => NemotronRnntEngine.Create(directory, wrongEncoder, options));
        Assert.Contains("no_such_output", joint.Message);
        Assert.Contains("no_such_cache", encoder.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => NemotronRnntEngine.Create(directory, config, new NemotronEngineOptions("cpu", CpuThreads: 0)));

        using var engine = NemotronRnntEngine.Create(directory, config, options);
        Assert.Equal(config.ChunkSamples, engine.ChunkSamples);
    }

    [NemotronModelFact]
    public void Recognizer_WhenTheFolderCannotBeLoaded_ShouldThrowAndLeaveNothingLocked()
    {
        models.ReleaseAll();
        var missing = Path.Combine(Path.GetTempPath(), "nemotron-no-such-model-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => new NemotronRnntRecognizer(missing));
        var directory = NemotronTestEnvironment.LocateModel(Variant)!;
        using var recognizer = new NemotronRnntRecognizer(directory, "cpu", null, false, new NemotronRecognizerOptions { CpuThreads = 2 });
        Assert.False(recognizer.IsFaulted);
    }

    // ── non-finite input on the real model ──────────────────────────────

    [NemotronModelFact]
    public void NonFiniteSamples_ShouldNotDisturbTheRecognitionAfterThem()
    {
        var recognizer = models.Get(Variant);
        var golden = GoldenCase.Get($"{Variant}_en0001");
        var audio = golden.LoadAudio();
        var countedBefore = recognizer.NonFiniteSamples;                 // cumulative over the recognizer's life
        var damaged = (float[])audio.Clone();
        damaged[40000] = float.NaN;                                      // a glitch of a faulty source in the middle of the clip
        damaged[40001] = float.PositiveInfinity;

        var replay = Replay.Run(recognizer, damaged, recognizer.ChunkSamples);

        Assert.Equal(2, recognizer.NonFiniteSamples - countedBefore);
        var engine = (NemotronRnntEngine)recognizer.Engine;
        Assert.Equal(0, engine.NonFiniteJoints);                         // nothing non-finite ever reached the encoder
        Assert.All(replay.Tokens, token => Assert.True(token.Id != 0, "an <unk> token"));
        // two zeroed samples (125 us) cannot change the words: the transcript stays that of the clean clip
        var distance = WordText.Distance(WordText.Words(golden.Text), WordText.Words(replay.Text.ToString()));
        Assert.True(distance <= 1, $"two bad samples moved the transcript by {distance} words: {replay.Text}");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void Collect(Replay replay, NemotronRnntRecognizer recognizer, float[] piece) => replay.Collect(recognizer, recognizer.ProcessAudio(piece));

    private void AssertSensibleStream(NemotronRnntRecognizer recognizer, int audioSamples, Replay replay, string reference, double allowedDistanceShare, string name)
    {
        var n = recognizer.ChunkSamples;
        Assert.Equal((audioSamples + n - 1) / n, recognizer.ChunksConsumed);
        Assert.Equal(0, recognizer.BufferedSamples);
        Assert.False(recognizer.IsFaulted);
        ParityAssert.TimelineInvariants(recognizer, replay.Tokens, audioSamples);

        var expected = WordText.Words(WordText.WithoutLanguageTags(reference));
        var distance = WordText.Distance(expected, WordText.Words(WordText.WithoutLanguageTags(replay.Text.ToString())));
        output.WriteLine($"{name}: word distance {distance} of {expected.Length}; got \"{replay.Text}\"; reference \"{reference}\"");
        Assert.True(distance <= Math.Max(2, expected.Length * allowedDistanceShare), $"{name}: text is off by {distance} of {expected.Length} words: {replay.Text}");
    }
}

/// <summary>
/// The golden folders and the opt-ins that ask for them: when the environment asks for a bigger set, that set has to be able
/// to run, otherwise a run would be green without having replayed anything.
/// </summary>
public sealed class E2E_NemotronGoldenInventoryTests(ITestOutputHelper output)
{
    [Fact]
    public void ARequestedGoldenSet_ShouldHaveRunnableCases()
    {
        var load = GoldenCase.LoadDefault();
        Assert.Empty(load.Problems);                                    // an unreadable file or a missing requested folder always fails

        if (!GoldenCase.ExtraSetRequested)
        {
            output.WriteLine("No extra golden set requested (NEMOTRON_FULL_GOLDEN, NEMOTRON_GOLDEN_DIR): only the repository set applies.");
            return;
        }

        var all = load.Cases;
        var runnable = all.Where(c => c.WhyNotRunnable() is null).ToList();
        var skipped = all.Select(c => (Case: c, Why: c.WhyNotRunnable())).Where(x => x.Why is not null).ToList();
        output.WriteLine($"{all.Count} golden cases, {runnable.Count} runnable, {skipped.Count} skipped");
        foreach (var group in skipped.GroupBy(x => x.Why).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {group.Count()} skipped: {group.Key} (first: {group.First().Case.Name})");

        Assert.True(runnable.Any(c => !c.Vad), "an extra golden set was requested but none of its parity cases can run here: check the model folders and the audio (NEMOTRON_GOLDEN_AUDIO, SPEECHLIB_TEST_AUDIO)");
        Assert.True(runnable.Any(c => c.Vad), "an extra golden set was requested but none of its VAD cases can run here");
    }
}
