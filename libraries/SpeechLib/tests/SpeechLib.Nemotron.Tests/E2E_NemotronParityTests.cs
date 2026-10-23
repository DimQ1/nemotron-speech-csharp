using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// Token-for-token parity of <see cref="NemotronRnntRecognizer"/> (plain ONNX Runtime) with ORT GenAI 0.16.0, replaying
/// the recorded GenAI runs (golden JSON files, see <see cref="GoldenCase"/>) on the real models. Skipped without the
/// models or the test audio.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronParityTests(NemotronModelCache models, ITestOutputHelper output)
{
    private static int _cases, _chunks, _tokens;

    [NemotronGoldenTheory]
    [MemberData(nameof(GoldenData.Parity), MemberType = typeof(GoldenData))]
    public void Tokens_ShouldMatchGenAiChunkByChunk(string variant, string name)
    {
        var golden = GoldenCase.Get(name);
        var recognizer = models.Get(variant);
        Assert.Equal(golden.ChunkSamples, recognizer.ChunkSamples);
        SetLanguage(recognizer, golden);
        var audio = golden.LoadAudio();

        var replay = Replay.Run(recognizer, audio, recognizer.ChunkSamples);   // the same N-sized pieces the oracle fed

        var (chunks, tokens) = ParityAssert.MatchesGolden(golden, audio.Length, replay);
        ParityAssert.TimelineInvariants(recognizer, replay.Tokens, audio.Length);

        Interlocked.Increment(ref _cases);
        Interlocked.Add(ref _chunks, chunks);
        Interlocked.Add(ref _tokens, tokens);
        output.WriteLine($"parity {name}: {chunks} chunks, {tokens} tokens identical to GenAI (running total {_cases} clips, {_chunks} chunks, {_tokens} tokens)");
    }

    [NemotronGoldenTheory]
    [MemberData(nameof(GoldenData.Subset), MemberType = typeof(GoldenData))]
    public void RaggedFeeding_ShouldGiveTheSameTokensAsAlignedFeeding(string variant, string name)
    {
        var golden = GoldenCase.Get(name);
        var recognizer = models.Get(variant);
        SetLanguage(recognizer, golden);
        var audio = golden.LoadAudio();

        var aligned = Replay.Run(recognizer, audio, recognizer.ChunkSamples);
        ParityAssert.MatchesGolden(golden, audio.Length, aligned);

        var schedules = new Dictionary<string, Func<IEnumerable<float[]>>>
        {
            ["3200 samples (the 200 ms capture batch)"] = () => TestAudio.Split(audio, 3200),
            ["800 samples (the 50 ms capture batch)"] = () => TestAudio.Split(audio, 800),
            ["everything in one call"] = () => [audio],
            ["odd sizes 1, 7, 4999, 20001, 333"] = () => TestAudio.Split(audio, 1, 7, 4999, 20001, 333),
            ["the golden baseline's irregular cycle"] = () => TestAudio.Split(audio, 800, 3200, 160, 5000, 8960, 1, 2400, 7200, 320, 4480),
            ["random 1 .. 3 chunks (seed 11)"] = () => RandomSplit(audio, new Random(11), 3 * recognizer.ChunkSamples),
        };
        foreach (var (label, pieces) in schedules)
        {
            recognizer.ResetStreamingState();
            var ragged = Replay.Run(recognizer, pieces());

            Assert.True(aligned.Positions().SequenceEqual(ragged.Positions()), $"{name}, {label}: the tokens or their positions differ from aligned feeding");
            Assert.Equal(aligned.Text.ToString(), ragged.Text.ToString());
            Assert.Equal(aligned.IdsByChunk().Keys.OrderBy(k => k), ragged.IdsByChunk().Keys.OrderBy(k => k));
        }
    }

    [NemotronModelFact]
    public void ResetStreamingState_ShouldMakeAClipIndependentOfThePreviousOne()
    {
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);
        var clipA = LoadClip("cv17/en/0001.wav");
        var clipB = LoadClip("cv17/en/0002.wav");

        var alone = Replay.Run(recognizer, clipB, recognizer.ChunkSamples);

        recognizer.ResetStreamingState();
        Replay.Run(recognizer, clipA, recognizer.ChunkSamples);                    // A, flushed: the decoder and caches hold A's state
        recognizer.ResetStreamingState();
        var afterReset = Replay.Run(recognizer, clipB, recognizer.ChunkSamples);

        Assert.NotEmpty(alone.Tokens);
        Assert.True(alone.Positions().SequenceEqual(afterReset.Positions()));
        Assert.Equal(alone.Text.ToString(), afterReset.Text.ToString());
    }

    [NemotronModelFact]
    public void WithoutAReset_TheStateOfThePreviousStreamLeaksIntoTheNext()
    {
        // The negative control behind the test above (and the ModelSession bug A4 measured): stream state carries on.
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);
        var clipA = LoadClip("cv17/en/0001.wav");
        var clipB = LoadClip("cv17/en/0002.wav");

        var alone = Replay.Run(recognizer, clipB, recognizer.ChunkSamples);
        recognizer.ResetStreamingState();
        Replay.Run(recognizer, clipA, recognizer.ChunkSamples);
        var continued = Replay.Run(recognizer, clipB, recognizer.ChunkSamples);   // no reset in between

        Assert.False(alone.Tokens.Select(t => t.Id).SequenceEqual(continued.Tokens.Select(t => t.Id)),
            "clip B decoded identically after clip A without a reset: the control no longer shows the leak");
    }

    [NemotronModelFact]
    public void ResetStreamingState_ShouldAlsoDropBufferedAudioAndTheTimeline()
    {
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);
        var clipA = LoadClip("cv17/en/0001.wav");
        var clipB = LoadClip("cv17/en/0002.wav");

        var alone = Replay.Run(recognizer, clipB, recognizer.ChunkSamples);

        recognizer.ResetStreamingState();
        recognizer.ProcessAudio(clipA[..(2 * recognizer.ChunkSamples + 1234)]);   // two chunks consumed, 1234 samples buffered
        Assert.Equal(1234, recognizer.BufferedSamples);
        recognizer.ResetStreamingState();
        Assert.Equal(0, recognizer.BufferedSamples);
        Assert.Equal(0, recognizer.ChunksConsumed);
        var afterReset = Replay.Run(recognizer, clipB, recognizer.ChunkSamples);

        Assert.True(alone.Positions().SequenceEqual(afterReset.Positions()));
    }

    [NemotronModelFact]
    public void PredictionNetwork_ShouldRunOnlyAfterEmittedTokens()
    {
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);
        var engine = (NemotronRnntEngine)recognizer.Engine;
        var clip = LoadClip("cv17/en/0001.wav");
        recognizer.ResetStreamingState();
        var predictionsBefore = engine.PredictionCalls;
        var jointsBefore = engine.JointCalls;

        var replay = Replay.Run(recognizer, clip, recognizer.ChunkSamples);

        var predictions = engine.PredictionCalls - predictionsBefore;
        var joints = engine.JointCalls - jointsBefore;
        var frames = replay.Chunks.Count * (long)recognizer.FramesPerChunk;
        Assert.NotEmpty(replay.Tokens);
        Assert.InRange(predictions, 1, replay.Tokens.Count + 1);          // once at the start, once after each token
        Assert.InRange(joints, frames, frames + replay.Tokens.Count);     // one per frame, one more per token
        // GenAI runs the prediction network once per joint call: this loop needs well under half as many runs
        Assert.True(predictions * 2 < joints, $"{predictions} prediction runs for {joints} joint runs");
        output.WriteLine($"prediction runs {predictions}, joint runs {joints} (GenAI: {joints} each), {replay.Tokens.Count} tokens, {frames} frames");
    }

    [NemotronModelFact]
    public void Chunks_ShouldNotAllocateTheEncoderCachesOnTheManagedHeap()
    {
        // The encoder caches are 6 MB per direction. They live in two buffers bound as input and output and swap places,
        // so a chunk costs only the small bookkeeping objects of the calls (and the text), never megabytes.
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);
        var clip = LoadClip("cv17/en/0001.wav");
        var n = recognizer.ChunkSamples;
        recognizer.ProcessAudio(clip.AsSpan(0, 3 * n));                      // warm up: JIT, first-use buffers

        const int chunks = 6;
        var before = GC.GetAllocatedBytesForCurrentThread();
        recognizer.ProcessAudio(clip.AsSpan(3 * n, chunks * n));
        var perChunk = (GC.GetAllocatedBytesForCurrentThread() - before) / chunks;

        output.WriteLine($"managed allocation per chunk: {perChunk} bytes");
        Assert.True(perChunk < 64 * 1024, $"{perChunk} bytes per chunk");
    }

    [NemotronModelFact]
    public void Flush_ShouldPadTheRemainderAndNotAddATailChunkForAWholeNumberOfChunks()
    {
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);
        var clip = LoadClip("cv17/en/0001.wav");
        var n = recognizer.ChunkSamples;

        var exact = clip[..(4 * n)];
        recognizer.ProcessAudio(exact);
        Assert.Equal(4, recognizer.ChunksConsumed);
        Assert.Null(recognizer.Flush());
        Assert.Equal(4, recognizer.ChunksConsumed);          // no extra chunk after a whole number of chunks

        recognizer.ResetStreamingState();
        recognizer.ProcessAudio(clip[..(4 * n + 1)]);
        Assert.Equal(4, recognizer.ChunksConsumed);
        Assert.NotNull(recognizer.Flush());                  // one sample is padded to a whole chunk and encoded
        Assert.Equal(5, recognizer.ChunksConsumed);
    }

    [NemotronModelFact]
    public void SingleLanguageControls_ShouldWork()
    {
        var recognizer = models.Get(NemotronTestEnvironment.DefaultVariant);

        Assert.False(recognizer.IsSingleLanguage);
        Assert.True(recognizer.TrySetLanguage("ru"));
        Assert.Equal(11, recognizer.LanguageId);
        Assert.True(recognizer.TrySetLanguage("0"));
        Assert.Equal(0, recognizer.LanguageId);
        Assert.False(recognizer.TrySetLanguage("klingon"));
        Assert.Equal("false", recognizer.VadStatus);
        Assert.Equal(0f, recognizer.BlankPenalty);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static float[] LoadClip(string relative) => NemotronTestEnvironment.LoadAudioRelative(relative);

    private static void SetLanguage(NemotronRnntRecognizer recognizer, GoldenCase golden)
    {
        Assert.True(recognizer.TrySetLanguage(golden.LangId ?? "0"), $"language {golden.LangId}");
        Assert.Equal(int.Parse(golden.LangId ?? "0"), recognizer.LanguageId);
    }

    private static IEnumerable<float[]> RandomSplit(float[] audio, Random rng, int maxPiece)
    {
        var position = 0;
        while (position < audio.Length)
        {
            var size = Math.Min(rng.Next(1, maxPiece + 1), audio.Length - position);
            yield return audio[position..(position + size)];
            position += size;
        }
    }
}
