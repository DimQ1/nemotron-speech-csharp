using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The silence drop rule against GenAI's native VAD (<c>use_vad</c>) on scenario audio (clips, pauses of room tone or
/// digital silence): the same chunks must be dropped and the same tokens emitted. Skipped without the models, the
/// Silero model of the folder or the test audio.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronVadTests(NemotronModelCache models, ITestOutputHelper output)
{
    [NemotronGoldenTheory(vad: true)]
    [MemberData(nameof(GoldenData.Vad), MemberType = typeof(GoldenData))]
    public void DroppedChunksAndTokens_ShouldMatchGenAiNativeVad(string variant, string name)
    {
        var golden = GoldenCase.Get(name);
        var recognizer = models.Get(variant);
        Assert.True(recognizer.TrySetVad(true));
        Assert.Equal("true", recognizer.VadStatus);
        Assert.True(recognizer.TrySetLanguage(golden.LangId ?? "0"));
        var audio = golden.LoadAudio();

        // the granularity the oracle was fed with (chunk aligned unless the golden says otherwise)
        var replay = Replay.Run(recognizer, audio, golden.FeedSamples > 0 ? golden.FeedSamples : recognizer.ChunkSamples);

        var (chunks, tokens) = ParityAssert.MatchesGolden(golden, audio.Length, replay);
        ParityAssert.TimelineInvariants(recognizer, replay.Tokens, audio.Length);

        // The clock never loses a dropped chunk: encoded + dropped chunks account for every chunk of the grid.
        var grid = (audio.Length + recognizer.ChunkSamples - 1) / recognizer.ChunkSamples;
        Assert.Equal(grid, replay.Chunks.Count);
        Assert.Equal(Enumerable.Range(0, grid), replay.Chunks.Select(c => c.Index));
        Assert.Equal(replay.DroppedChunks.Count(), recognizer.ChunksDropped);
        output.WriteLine($"vad {name}: {grid} chunks, {recognizer.ChunksDropped} dropped ({Ranges(replay.DroppedChunks)}), {tokens} tokens, identical to GenAI");
    }

    [NemotronGoldenTheory(vad: true)]
    [MemberData(nameof(GoldenData.Vad), MemberType = typeof(GoldenData))]
    public void Drops_ShouldNotDependOnHowTheAudioIsBatched(string variant, string name)
    {
        var golden = GoldenCase.Get(name);
        var recognizer = models.Get(variant);
        Assert.True(recognizer.TrySetVad(true));
        Assert.True(recognizer.TrySetLanguage(golden.LangId ?? "0"));
        var audio = golden.LoadAudio();
        var aligned = Replay.Run(recognizer, audio, recognizer.ChunkSamples);

        foreach (var piece in new[] { 800, 3200, 12345 })
        {
            recognizer.ResetStreamingState();
            var live = Replay.Run(recognizer, audio, piece);

            Assert.Equal(aligned.DroppedChunks, live.DroppedChunks);
            Assert.True(aligned.Positions().SequenceEqual(live.Positions()), $"{name}, {piece}-sample batches: tokens differ");
            Assert.Equal(aligned.Text.ToString(), live.Text.ToString());
        }
    }

    [NemotronModelFact]
    public void VadOn_OnShortSpeechClips_ShouldChangeNothing()
    {
        // clips of a few seconds never hold six silent chunks in a row: the gate keeps everything
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);
        var audio = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0001.wav");
        var without = Replay.Run(recognizer, audio, recognizer.ChunkSamples);

        recognizer.ResetStreamingState();
        Assert.True(recognizer.TrySetVad(true));
        var with = Replay.Run(recognizer, audio, recognizer.ChunkSamples);

        Assert.Empty(with.DroppedChunks);
        Assert.True(without.Positions().SequenceEqual(with.Positions()));
        Assert.Equal(6, recognizer.SilenceDropThreshold);
    }

    [NemotronModelFact]
    public void Gate_ShouldSeparateSpeechFromSilenceAtTheModelFoldersThreshold()
    {
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        var config = NemotronModelConfig.Load(directory);
        var speech = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0001.wav");
        const int n = 8960;

        using var gate = new SileroChunkGate(Path.Combine(directory, config.Vad!.FileName), config.Vad.Threshold);

        Assert.Equal(0.3f, gate.Threshold);
        Assert.True(gate.ContainsSpeech(speech.AsSpan(n, n)));                                    // 0.56 - 1.12 s: the first words
        gate.Reset();
        Assert.False(gate.ContainsSpeech(new float[n]));                                          // digital silence
        gate.Reset();
        Assert.False(gate.ContainsSpeech(SyntheticAudio.RoomTone(n, 0.002f, 5)));                 // room tone
        gate.Reset();
        Assert.True(gate.ContainsSpeech(speech.AsSpan(n, n - 100)));                              // a partial window at the end is padded and scored
    }

    [NemotronModelFact]
    public void Gate_StopsAtTheFirstSpeechWindowLikeGenAi()
    {
        // Upstream returns at the first window above the threshold: the recurrent state and the context stay where that
        // window left them, so the next chunk is scored from a different state than after a full scan of the chunk.
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        var config = NemotronModelConfig.Load(directory);
        var speech = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0001.wav");
        const int n = 8960;
        var chunk = speech.AsSpan(n, n);
        var probe = speech.AsSpan(2 * n, SileroChunkGate.WindowSamples);

        using var gate = new SileroChunkGate(Path.Combine(directory, config.Vad!.FileName), 0.3f);

        Assert.True(gate.ContainsSpeech(chunk));                  // early exit
        gate.Threshold = 2f;                                      // from now on never "speech": every window is scored
        gate.ContainsSpeech(probe);
        var afterEarlyExit = gate.LastProbability;

        gate.Reset();
        Assert.False(gate.ContainsSpeech(chunk));                 // full scan of the same chunk, 17 windows + the padded rest
        gate.ContainsSpeech(probe);
        var afterFullScan = gate.LastProbability;

        Assert.NotEqual(afterEarlyExit, afterFullScan);
    }

    [NemotronModelFact]
    public void Gate_ThresholdOutsideZeroToOne_ShouldBeClampedLikeUpstream()
    {
        // the config reader accepts any threshold (as upstream does); the gate clamps it to [0, 1]
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        var path = Path.Combine(directory, NemotronModelConfig.Load(directory).Vad!.FileName);

        using (var high = new SileroChunkGate(path, 1.5f))
            Assert.Equal(1f, high.Threshold);
        using (var low = new SileroChunkGate(path, -0.2f))
            Assert.Equal(0f, low.Threshold);
        using (var normal = new SileroChunkGate(path, 0.3f))
            Assert.Equal(0.3f, normal.Threshold);
    }

    [NemotronModelFact]
    public void ExternalVadFilter_ShouldReplaceTheNativeGate()
    {
        models.ReleaseAll();                                       // this test loads a recognizer of its own
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        var filter = new AlwaysSilent();
        using var recognizer = new NemotronRnntRecognizer(directory, "cpu", null, useVad: true,
            new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads, VadFilter = filter });

        recognizer.ProcessAudio(new float[8 * recognizer.ChunkSamples]);

        Assert.True(recognizer.VadEnabled);
        Assert.Equal(8, filter.Calls);
        Assert.Equal(3, recognizer.ChunksDropped);                 // chunks 5, 6, 7: the 6th silent chunk onward
        Assert.False(filter.Disposed);                              // the caller owns an injected filter
    }

    private sealed class AlwaysSilent : SpeechLib.Audio.IVadFilter
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public float LastProbability => 0f;
        public bool HasSpeech(ReadOnlySpan<float> samples)
        {
            Calls++;
            return false;
        }
        public void Reset() { }
        public void Dispose() => Disposed = true;
    }

    private static string Ranges(IEnumerable<int> indices)
    {
        var list = indices.OrderBy(i => i).ToList();
        if (list.Count == 0)
            return "none";
        var parts = new List<string>();
        for (var i = 0; i < list.Count;)
        {
            var j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1)
                j++;
            parts.Add(i == j ? $"{list[i]}" : $"{list[i]}-{list[j]}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}
