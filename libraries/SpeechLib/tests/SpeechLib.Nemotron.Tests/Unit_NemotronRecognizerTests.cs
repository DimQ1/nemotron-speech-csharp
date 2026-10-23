using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The recognizer's own logic (buffering into chunks, flush, silence drop, timeline, reset) over a fake engine and
/// a fake speech gate, so none of it needs a model.
/// </summary>
public sealed class Unit_NemotronRecognizerTests
{
    private const int N = 8960;       // 560 ms chunk
    private const int Frames = 7;

    private static readonly Lazy<NemotronVocabulary> VocabularyOf = new(() =>
        NemotronVocabulary.FromPieces(NemotronTestEnvironment.ReadVocabularyLines(), 13087, 13088));

    private sealed class Rig : IDisposable
    {
        public required NemotronRnntRecognizer Recognizer { get; init; }
        public required FakeChunkEngine Engine { get; init; }
        public required List<FakeGate> Gates { get; init; }
        public FakeGate? Gate => Gates.LastOrDefault();
        public void Dispose() => Recognizer.Dispose();
    }

    private static Rig Make(int chunk = N, int frames = Frames, int lookahead = 0, bool useVad = false, Func<int, bool>? speech = null,
        string? langId = null, bool supportsLanguage = true, Func<IChunkSpeechGate>? gateFactory = null, int closing = 0)
    {
        var variant = chunk == 17920 ? "int4-c112" : "int4-c056";
        var engine = new FakeChunkEngine(chunk, frames, lookahead, supportsLanguage);
        var gates = new List<FakeGate>();
        speech ??= _ => true;
        gateFactory ??= () =>
        {
            var gate = new FakeGate(chunk, speech);
            gates.Add(gate);
            return gate;
        };
        var recognizer = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig(variant), VocabularyOf.Value, gateFactory, useVad, langId,
            new NemotronRecognizerOptions { FlushClosingChunks = closing });
        return new Rig { Recognizer = recognizer, Engine = engine, Gates = gates };
    }

    private static int Id(string piece)
    {
        var id = Array.IndexOf(NemotronTestEnvironment.ReadVocabularyLines(), piece);
        return id >= 0 ? id : throw new InvalidOperationException($"'{piece}' is not in the vocabulary");
    }

    // ── identity ────────────────────────────────────────────────────────

    [Fact]
    public void ExecutionProvider_ShouldReportWhatTheEngineReports_AlsoAfterItChanged()
    {
        using var rig = Make();
        Assert.Equal("cpu", rig.Recognizer.ExecutionProvider.Effective);

        var onGpu = new NemotronProviderInfo("webgpu:hp", true, "NVIDIA GeForce", null);
        rig.Engine.Provider = onGpu;
        Assert.Same(onGpu, rig.Recognizer.ExecutionProvider);

        var fallback = NemotronProviderInfo.Cpu("webgpu:hp", "a run failed");
        rig.Engine.Provider = fallback;
        Assert.Same(fallback, rig.Recognizer.ExecutionProvider);
    }

    // ── buffering ───────────────────────────────────────────────────────

    [Fact]
    public void ProcessAudio_LessThanAChunk_ShouldReturnNullAndBuffer()
    {
        using var rig = Make();

        var text = rig.Recognizer.ProcessAudio(TestAudio.Ramp(N - 1));

        Assert.Null(text);
        Assert.Empty(rig.Engine.Calls);
        Assert.Equal(N - 1, rig.Recognizer.BufferedSamples);
        Assert.Equal(0, rig.Recognizer.ChunksConsumed);
        Assert.Equal(0, rig.Recognizer.LastTokenCount);
        Assert.Empty(rig.Recognizer.LastChunks);
    }

    [Fact]
    public void ProcessAudio_ExactlyAChunk_ShouldEncodeItAndReturnEmptyText()
    {
        using var rig = Make();
        var audio = TestAudio.Ramp(N);

        var text = rig.Recognizer.ProcessAudio(audio);

        Assert.Equal("", text);                          // encoded, no tokens: empty, not null
        var call = Assert.Single(rig.Engine.Calls);
        Assert.Equal(0, call.ChunkIndex);
        Assert.Equal(audio, call.Chunk);
        Assert.Equal(0, rig.Recognizer.BufferedSamples);
        Assert.Equal(1, rig.Recognizer.ChunksConsumed);
        Assert.Equal(N, rig.Recognizer.StreamPositionSamples);
    }

    [Fact]
    public void ProcessAudio_SeveralChunksInOneCall_ShouldEncodeAllOfThemInOrder()
    {
        using var rig = Make();
        var audio = TestAudio.Ramp(3 * N + 100);

        var text = rig.Recognizer.ProcessAudio(audio);

        Assert.Equal("", text);
        Assert.Equal([0, 1, 2], rig.Engine.Calls.Select(c => c.ChunkIndex));
        for (var k = 0; k < 3; k++)
            Assert.Equal(audio[(k * N)..((k + 1) * N)], rig.Engine.Calls[k].Chunk);
        Assert.Equal(100, rig.Recognizer.BufferedSamples);
        Assert.Equal([0, 1, 2], rig.Recognizer.LastChunks.Select(c => c.Index));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(800)]
    [InlineData(3200)]
    [InlineData(N - 1)]
    [InlineData(N)]
    [InlineData(N + 1)]
    [InlineData(2 * N + 5)]
    [InlineData(100000)]
    public void ProcessAudio_AnyBatchSize_ShouldEncodeTheSameChunks(int batch)
    {
        const int total = 5 * N + 1234;
        var audio = TestAudio.Ramp(total);
        using var rig = Make();

        foreach (var piece in TestAudio.Split(audio, batch))
            rig.Recognizer.ProcessAudio(piece);

        Assert.Equal([0, 1, 2, 3, 4], rig.Engine.Calls.Select(c => c.ChunkIndex));
        for (var k = 0; k < 5; k++)
            Assert.Equal(audio[(k * N)..((k + 1) * N)], rig.Engine.Calls[k].Chunk);
        Assert.Equal(1234, rig.Recognizer.BufferedSamples);
    }

    [Fact]
    public void ProcessAudio_RandomRaggedBatches_ShouldEncodeTheSameChunksAsAlignedFeeding()
    {
        var audio = TestAudio.Ramp(11 * N + 777);
        using var aligned = Make();
        foreach (var piece in TestAudio.Split(audio, N))
            aligned.Recognizer.ProcessAudio(piece);
        aligned.Recognizer.Flush();

        var rng = new Random(2024);
        for (var trial = 0; trial < 25; trial++)
        {
            using var ragged = Make();
            var position = 0;
            while (position < audio.Length)
            {
                var size = Math.Min(rng.Next(1, 3 * N), audio.Length - position);
                ragged.Recognizer.ProcessAudio(audio.AsSpan(position, size));
                position += size;
            }
            ragged.Recognizer.Flush();

            Assert.Equal(aligned.Engine.Calls.Count, ragged.Engine.Calls.Count);
            for (var i = 0; i < aligned.Engine.Calls.Count; i++)
            {
                Assert.Equal(aligned.Engine.Calls[i].ChunkIndex, ragged.Engine.Calls[i].ChunkIndex);
                Assert.Equal(aligned.Engine.Calls[i].Chunk, ragged.Engine.Calls[i].Chunk);
            }
        }
    }

    [Fact]
    public void ProcessAudio_ArrayAndSpanOverloads_ShouldAgree()
    {
        using var rig = Make();
        var audio = TestAudio.Ramp(N);

        var viaArray = rig.Recognizer.ProcessAudio(audio);

        using var other = Make();
        var viaSpan = other.Recognizer.ProcessAudio(audio.AsSpan());

        Assert.Equal(viaArray, viaSpan);
        Assert.Throws<ArgumentNullException>(() => rig.Recognizer.ProcessAudio((float[])null!));
    }

    // ── text and tokens ─────────────────────────────────────────────────

    [Fact]
    public void ProcessAudio_Tokens_ShouldBecomeTextAndCarryTheirPositions()
    {
        using var rig = Make();
        rig.Engine.Script = (chunk, _) => chunk switch
        {
            0 => [(Id("▁Bu"), 2), (Id("st"), 2), (Id("er"), 5)],
            1 => [],
            _ => [(Id("."), 0), (2, 0), (Id("<ru-RU>"), 0)],
        };

        var first = rig.Recognizer.ProcessAudio(TestAudio.Ramp(N));
        Assert.Equal(" Buster", first);
        Assert.Equal(3, rig.Recognizer.LastTokenCount);
        Assert.Equal([(0, 2, 0, 2L), (0, 2, 1, 2L), (0, 5, 0, 5L)],
            rig.Recognizer.LastTokens.Select(t => (t.Chunk, t.Frame, t.Symbol, t.GlobalFrame)));

        Assert.Equal("", rig.Recognizer.ProcessAudio(TestAudio.Ramp(N, N)));
        Assert.Equal(0, rig.Recognizer.LastTokenCount);
        Assert.Empty(rig.Recognizer.LastTokens);                   // each call describes only itself

        var third = rig.Recognizer.ProcessAudio(TestAudio.Ramp(N, 2 * N));
        Assert.Equal(". <ru-RU>", third);                        // tags stay in the text, like GenAI
        Assert.Equal([2L * Frames, 2L * Frames, 2L * Frames], rig.Recognizer.LastTokens.Select(t => t.GlobalFrame));
        Assert.Equal([0, 1, 2], rig.Recognizer.LastTokens.Select(t => t.Symbol));
        var info = Assert.Single(rig.Recognizer.LastChunks);
        Assert.Equal(new NemotronChunkInfo(2, false, 0, 3), info);
    }

    [Fact]
    public void ProcessAudio_SeveralChunksInOneCall_ShouldConcatenateTheirTextAndNumberTheirTokens()
    {
        using var rig = Make();
        rig.Engine.Script = (chunk, _) => [(Id("▁a"), 1), (Id("▁b"), 3)];

        var text = rig.Recognizer.ProcessAudio(TestAudio.Ramp(3 * N));

        Assert.Equal(" a b a b a b", text);
        Assert.Equal(6, rig.Recognizer.LastTokenCount);
        Assert.Equal([(0, 0, 2), (1, 2, 2), (2, 4, 2)],
            rig.Recognizer.LastChunks.Select(c => (c.Index, c.FirstToken, c.TokenCount)));
        Assert.Equal([1L, 3L, 8L, 10L, 15L, 17L], rig.Recognizer.LastTokens.Select(t => t.GlobalFrame));
    }

    // ── flush ───────────────────────────────────────────────────────────

    [Fact]
    public void Flush_Remainder_ShouldBeZeroPaddedToAChunk()
    {
        using var rig = Make();
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N + 1000));
        rig.Engine.Calls.Clear();
        rig.Engine.Script = (_, _) => [(Id("▁go"), 6)];

        var text = rig.Recognizer.Flush();

        Assert.Equal(" go", text);
        var call = Assert.Single(rig.Engine.Calls);
        Assert.Equal(1, call.ChunkIndex);
        Assert.Equal(TestAudio.Ramp(1000, N), call.Chunk[..1000]);
        Assert.All(call.Chunk[1000..], v => Assert.Equal(0f, v));
        Assert.Equal(N, call.Chunk.Length);
        Assert.Equal(0, rig.Recognizer.BufferedSamples);
        Assert.Equal(2, rig.Recognizer.ChunksConsumed);
        Assert.Equal(1L * Frames + 6, rig.Recognizer.LastTokens.Single().GlobalFrame);
    }

    [Fact]
    public void Flush_EmptyBuffer_ShouldNotAddATailChunk()
    {
        using var rig = Make();
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(2 * N));
        rig.Engine.Calls.Clear();

        Assert.Null(rig.Recognizer.Flush());

        Assert.Empty(rig.Engine.Calls);
        Assert.Equal(2, rig.Recognizer.ChunksConsumed);
        Assert.Equal(0, rig.Recognizer.LastTokenCount);
    }

    [Fact]
    public void Flush_OnAFreshRecognizer_ShouldReturnNull()
    {
        using var rig = Make();

        Assert.Null(rig.Recognizer.Flush());
        Assert.Empty(rig.Engine.Calls);
    }

    [Fact]
    public void Flush_SingleSample_ShouldStillBeEncodedAsAWholeChunk()
    {
        using var rig = Make();
        rig.Recognizer.ProcessAudio([0.25f]);

        Assert.Equal("", rig.Recognizer.Flush());

        var call = Assert.Single(rig.Engine.Calls);
        Assert.Equal(0.25f, call.Chunk[0]);
        Assert.All(call.Chunk[1..], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ProcessAudio_AfterFlush_ShouldContinueOnTheNextChunkOfTheGrid()
    {
        using var rig = Make();
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N + 500));
        rig.Recognizer.Flush();                           // chunk 1 = 500 samples + padding
        rig.Engine.Calls.Clear();

        var next = TestAudio.Ramp(N, 99000);
        rig.Recognizer.ProcessAudio(next);

        var call = Assert.Single(rig.Engine.Calls);
        Assert.Equal(2, call.ChunkIndex);                 // the padded chunk keeps its place on the timeline
        Assert.Equal(next, call.Chunk);
        Assert.Equal(3L * N, rig.Recognizer.StreamPositionSamples);
    }

    // ── flush: closing chunks ───────────────────────────────────────────

    [Fact]
    public void Flush_WithClosingChunks_ShouldEncodeZeroChunksAfterThePaddedRemainderAndReturnTheirTokens()
    {
        using var rig = Make(closing: 1);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N + 1000));
        rig.Engine.Calls.Clear();
        rig.Engine.Script = (index, _) => index == 2 ? [(Id("▁go"), 1)] : [];   // the late last word

        var text = rig.Recognizer.Flush();

        Assert.Equal(" go", text);
        Assert.Equal([1, 2], rig.Engine.Calls.Select(c => c.ChunkIndex));
        Assert.Equal(TestAudio.Ramp(1000, N), rig.Engine.Calls[0].Chunk[..1000]);
        Assert.All(rig.Engine.Calls[1].Chunk, v => Assert.Equal(0f, v));
        Assert.Equal(3, rig.Recognizer.ChunksConsumed);
        Assert.Equal([1, 2], rig.Recognizer.LastChunks.Select(c => c.Index));
        Assert.Equal(2L * Frames + 1, rig.Recognizer.LastTokens.Single().GlobalFrame);
        Assert.Equal(1, rig.Recognizer.FlushClosingChunks);
    }

    [Fact]
    public void Flush_WithClosingChunks_AndAnEmptyRemainder_ShouldStillCloseTheLastEncodedChunk()
    {
        using var rig = Make(closing: 2);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(2 * N));
        rig.Engine.Calls.Clear();

        Assert.Equal("", rig.Recognizer.Flush());

        Assert.Equal([2, 3], rig.Engine.Calls.Select(c => c.ChunkIndex));
        Assert.Equal(4, rig.Recognizer.ChunksConsumed);
    }

    [Fact]
    public void Flush_WithClosingChunks_ASecondFlushOrAFreshRecognizer_ShouldAddNothing()
    {
        using var rig = Make(closing: 1);
        Assert.Null(rig.Recognizer.Flush());
        Assert.Empty(rig.Engine.Calls);

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N + 10));
        rig.Recognizer.Flush();
        rig.Engine.Calls.Clear();

        Assert.Null(rig.Recognizer.Flush());
        Assert.Empty(rig.Engine.Calls);
        Assert.Equal(3, rig.Recognizer.ChunksConsumed);
    }

    [Fact]
    public void Flush_WithClosingChunks_AfterADroppedChunk_ShouldAddNone()
    {
        // speech in chunk 0 only; silent chunks 1-6, chunk 6 is the 6th and is dropped: the model heard the pause already
        using var rig = Make(useVad: true, speech: index => index == 0, closing: 1);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(7 * N));
        Assert.Equal(1, rig.Recognizer.ChunksDropped);
        rig.Engine.Calls.Clear();

        Assert.Null(rig.Recognizer.Flush());

        Assert.Empty(rig.Engine.Calls);
    }

    [Fact]
    public void Flush_WithClosingChunks_ThenMoreAudio_ShouldContinueBehindThem()
    {
        using var rig = Make(closing: 1);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N + 500));
        rig.Recognizer.Flush();                           // chunk 1 = 500 samples + padding, chunk 2 = closing
        rig.Engine.Calls.Clear();

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N, 99000));

        Assert.Equal(3, Assert.Single(rig.Engine.Calls).ChunkIndex);
    }

    // ── reset ───────────────────────────────────────────────────────────

    [Fact]
    public void ResetStreamingState_ShouldClearEverythingOfTheStream()
    {
        using var rig = Make(useVad: true, speech: _ => true);
        rig.Engine.Script = (_, _) => [(Id("▁a"), 0)];
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(2 * N + 300));
        Assert.Equal(2, rig.Recognizer.ChunksConsumed);
        Assert.Equal(300, rig.Recognizer.BufferedSamples);

        rig.Recognizer.ResetStreamingState();

        Assert.Equal(1, rig.Engine.ResetCount);
        Assert.Equal(1, rig.Gate!.ResetCount);
        Assert.Equal(0, rig.Recognizer.ChunksConsumed);
        Assert.Equal(0, rig.Recognizer.ChunksDropped);
        Assert.Equal(0, rig.Recognizer.BufferedSamples);
        Assert.Equal(0, rig.Recognizer.LastTokenCount);
        Assert.Empty(rig.Recognizer.LastTokens);
        Assert.Empty(rig.Recognizer.LastChunks);
        Assert.Equal(0, rig.Recognizer.StreamPositionSamples);

        // the buffered 300 samples are gone: a new chunk is complete after exactly N new samples, indexed from 0
        rig.Engine.Calls.Clear();
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N - 1));
        Assert.Empty(rig.Engine.Calls);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(1, N - 1));
        Assert.Equal(0, Assert.Single(rig.Engine.Calls).ChunkIndex);
    }

    [Fact]
    public void ResetStreamingState_ShouldKeepLanguageAndTheVadSwitch()
    {
        using var rig = Make(useVad: true);
        Assert.True(rig.Recognizer.TrySetLanguage("ru"));

        rig.Recognizer.ResetStreamingState();

        Assert.Equal(11, rig.Recognizer.LanguageId);
        Assert.True(rig.Recognizer.VadEnabled);
    }

    [Fact]
    public void ResetStreamingState_AfterFlush_ShouldMakeTheRecognizerReusable()
    {
        using var rig = Make();
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N + 10));
        rig.Recognizer.Flush();
        rig.Recognizer.ResetStreamingState();
        rig.Engine.Calls.Clear();

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N));

        Assert.Equal(0, Assert.Single(rig.Engine.Calls).ChunkIndex);
    }

    // ── silence drop ────────────────────────────────────────────────────

    // Speech in chunks 0-2, silence from chunk 3 on, speech again in chunk 12.
    private static bool SpeechPattern(int chunk) => chunk <= 2 || chunk == 12;

    [Fact]
    public void SilenceDrop_ShouldDropFromTheSixthConsecutiveSilentChunkAndKeepCounting()
    {
        using var rig = Make(useVad: true, speech: SpeechPattern);
        rig.Engine.Script = (chunk, _) => [(Id("▁a"), 1)];

        var texts = new List<string?>();
        foreach (var piece in TestAudio.Split(TestAudio.Ramp(14 * N), N))
            texts.Add(rig.Recognizer.ProcessAudio(piece));

        // silent chunks 3..11: kept for the first five (3-7), dropped from the sixth (chunk 8) to the last silent one (11)
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 12, 13], rig.Engine.Calls.Select(c => c.ChunkIndex));
        Assert.Equal(4, rig.Recognizer.ChunksDropped);
        Assert.Equal(14, rig.Recognizer.ChunksConsumed);
        Assert.Equal(Enumerable.Range(0, 14), rig.Gate!.Scored);   // every chunk is scored, dropped or not
        Assert.All(new[] { 8, 9, 10, 11 }, i => Assert.Null(texts[i]));   // a dropped chunk looks like "no chunk yet"
        Assert.All(new[] { 0, 7, 12, 13 }, i => Assert.Equal(" a", texts[i]));
        Assert.Equal(6, rig.Recognizer.SilenceDropThreshold);
    }

    [Fact]
    public void SilenceDrop_ShouldKeepDroppedChunksOnTheTimeline()
    {
        using var rig = Make(useVad: true, speech: SpeechPattern);
        rig.Engine.Script = (chunk, _) => [(Id("▁a"), 1)];

        var tokens = new List<NemotronToken>();
        var infos = new List<NemotronChunkInfo>();
        foreach (var piece in TestAudio.Split(TestAudio.Ramp(14 * N), N))
        {
            rig.Recognizer.ProcessAudio(piece);
            tokens.AddRange(rig.Recognizer.LastTokens);
            infos.AddRange(rig.Recognizer.LastChunks);
        }

        var afterGap = tokens.Where(t => t.Chunk >= 12).ToList();
        Assert.Equal([12, 13], afterGap.Select(t => t.Chunk));
        Assert.Equal([12L * Frames + 1, 13L * Frames + 1], afterGap.Select(t => t.GlobalFrame));
        Assert.Equal(Enumerable.Range(8, 4), infos.Where(i => i.Dropped).Select(i => i.Index));
        Assert.All(infos.Where(i => i.Dropped), i => Assert.Equal(0, i.TokenCount));
        Assert.Equal(14L * N, rig.Recognizer.StreamPositionSamples);
    }

    [Fact]
    public void SilenceDrop_SpeechResetsTheCount()
    {
        // five silent, one speech, five silent: never reaches six in a row
        using var rig = Make(useVad: true, speech: c => c == 5);

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(11 * N));

        Assert.Equal(11, rig.Engine.Calls.Count);
        Assert.Equal(0, rig.Recognizer.ChunksDropped);
    }

    [Fact]
    public void SilenceDrop_LongChunks_ShouldDropFromTheThirdSilentChunk()
    {
        using var rig = Make(chunk: 17920, frames: 14, useVad: true, speech: c => c == 0);

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(8 * 17920));

        // silent chunks 1, 2 kept; the third (3) and all later ones dropped
        Assert.Equal([0, 1, 2], rig.Engine.Calls.Select(c => c.ChunkIndex));
        Assert.Equal(5, rig.Recognizer.ChunksDropped);
        Assert.Equal(3, rig.Recognizer.SilenceDropThreshold);
    }

    [Fact]
    public void SilenceDrop_Flush_ShouldNotApplyToThePaddedRemainder()
    {
        using var rig = Make(useVad: true, speech: _ => false);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(8 * N + 200));       // 8 silent chunks: the 6th, 7th and 8th dropped
        Assert.Equal(3, rig.Recognizer.ChunksDropped);
        rig.Engine.Calls.Clear();

        rig.Recognizer.Flush();

        var call = Assert.Single(rig.Engine.Calls);                      // processed although silent
        Assert.Equal(8, call.ChunkIndex);
        Assert.DoesNotContain(8, rig.Gate!.Scored);
    }

    [Fact]
    public void SilenceDrop_Disabled_ShouldEncodeEveryChunk()
    {
        using var rig = Make(useVad: false, speech: _ => false);

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(10 * N));

        Assert.Equal(10, rig.Engine.Calls.Count);
        Assert.Equal(0, rig.Recognizer.ChunksDropped);
        Assert.Empty(rig.Gates);
        Assert.Equal("false", rig.Recognizer.VadStatus);
    }

    [Fact]
    public void TrySetVad_ShouldCreateTheGateOnceAndDropItOnDisable()
    {
        var created = 0;
        FakeGate? gate = null;
        using var rig = Make(gateFactory: () => { created++; return gate = new FakeGate(N, _ => false); });

        Assert.True(rig.Recognizer.TrySetVad(true));
        Assert.True(rig.Recognizer.TrySetVad(true));       // already on: GenAI leaves the gate and its count alone
        Assert.Equal(1, created);
        Assert.Equal("true", rig.Recognizer.VadStatus);

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(7 * N));
        Assert.Equal(2, rig.Recognizer.ChunksDropped);

        Assert.True(rig.Recognizer.TrySetVad(false));
        Assert.True(gate!.Disposed);
        Assert.False(rig.Recognizer.VadEnabled);

        rig.Engine.Calls.Clear();
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N, 7 * N));
        Assert.Single(rig.Engine.Calls);                  // gate off: encoded again

        Assert.True(rig.Recognizer.TrySetVad(true));      // a fresh gate with a fresh count
        Assert.Equal(2, created);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(5 * N, 8 * N));
        Assert.Equal(2, rig.Recognizer.ChunksDropped);    // the new count starts at zero: five silent chunks drop nothing
    }

    [Fact]
    public void TrySetVad_WhenTheGateCannotBeCreated_ShouldReturnFalseAndStayOff()
    {
        using var rig = Make(gateFactory: () => throw new FileNotFoundException("no silero_vad.onnx"));

        Assert.False(rig.Recognizer.TrySetVad(true));

        Assert.False(rig.Recognizer.VadEnabled);
        Assert.Equal(0, rig.Recognizer.SilenceDropThreshold);
        Assert.True(rig.Recognizer.TrySetVad(false));
    }

    [Fact]
    public void Constructor_UseVadWithoutAGate_ShouldStartWithoutIt()
    {
        using var rig = Make(useVad: true, gateFactory: () => throw new FileNotFoundException("missing"));

        Assert.False(rig.Recognizer.VadEnabled);
        Assert.Equal("", rig.Recognizer.ProcessAudio(TestAudio.Ramp(N)));   // no throw, and a usable recognizer
    }

    // ── clean last frame (look-ahead) ───────────────────────────────────

    [Fact]
    public void LookaheadEngine_ShouldHoldAChunkBackUntilItsLookaheadArrived()
    {
        using var rig = Make(lookahead: 40);
        var audio = TestAudio.Ramp(2 * N + 100);

        Assert.Null(rig.Recognizer.ProcessAudio(audio[..(N + 39)]));
        Assert.Empty(rig.Engine.Calls);

        Assert.Equal("", rig.Recognizer.ProcessAudio(audio[(N + 39)..(N + 40)]));
        var call = Assert.Single(rig.Engine.Calls);
        Assert.Equal(0, call.ChunkIndex);
        Assert.Equal(audio[..N], call.Chunk);
        Assert.Equal(audio[N..(N + 40)], call.Lookahead);
        Assert.Equal(40, rig.Recognizer.BufferedSamples);   // the look-ahead stays buffered: it is the start of the next chunk
    }

    [Fact]
    public void LookaheadEngine_Flush_ShouldEncodeChunksStillWaitingForLookaheadWithWhatIsThere()
    {
        using var rig = Make(lookahead: 40);
        var audio = TestAudio.Ramp(N + 10);
        rig.Recognizer.ProcessAudio(audio);
        Assert.Empty(rig.Engine.Calls);                    // 10 of the 40 look-ahead samples only

        rig.Recognizer.Flush();

        Assert.Equal([0, 1], rig.Engine.Calls.Select(c => c.ChunkIndex));
        Assert.Equal(audio[N..], rig.Engine.Calls[0].Lookahead);        // the 10 real samples
        Assert.Equal(audio[N..], rig.Engine.Calls[1].Chunk[..10]);      // ... which are also the head of the padded chunk
        Assert.Empty(rig.Engine.Calls[1].Lookahead);
    }

    // ── language and runtime configuration ──────────────────────────────

    [Theory]
    [InlineData("ru", 11)]
    [InlineData("en-US", 0)]
    [InlineData("de", 9)]
    [InlineData("auto", 101)]
    [InlineData("42", 42)]
    public void TrySetLanguage_KnownLanguage_ShouldSetTheLangId(string language, int langId)
    {
        using var rig = Make();

        Assert.True(rig.Recognizer.TrySetLanguage(language));

        Assert.Equal(langId, rig.Recognizer.LanguageId);
        Assert.Equal(langId, rig.Engine.LanguageId);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("")]
    [InlineData("999")]
    public void TrySetLanguage_UnknownLanguage_ShouldBeRejected(string language)
    {
        using var rig = Make();

        Assert.False(rig.Recognizer.TrySetLanguage(language));
        Assert.Equal(0, rig.Recognizer.LanguageId);
    }

    [Fact]
    public void TrySetLanguage_ShouldTakeEffectAtTheNextEncoderCall()
    {
        using var rig = Make();
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N));
        rig.Recognizer.TrySetLanguage("ru");
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N, N));

        Assert.Equal([0, 11], rig.Engine.Calls.Select(c => c.Language));
    }

    [Fact]
    public void SingleLanguageEngine_ShouldRefuseLanguageChanges()
    {
        using var rig = Make(supportsLanguage: false, langId: "ru");

        Assert.True(rig.Recognizer.IsSingleLanguage);
        Assert.False(rig.Recognizer.TrySetLanguage("ru"));
        Assert.Equal(0, rig.Recognizer.LanguageId);
    }

    [Fact]
    public void Constructor_LangId_ShouldBeApplied()
    {
        using var rig = Make(langId: "ru");

        Assert.Equal(11, rig.Recognizer.LanguageId);
        Assert.False(rig.Recognizer.IsSingleLanguage);
    }

    [Fact]
    public void RuntimeConfigurable_SearchOptions_ShouldBeAcceptedAndIgnored()
    {
        using var rig = Make();
        IRuntimeConfigurable configurable = rig.Recognizer;

        Assert.True(configurable.TrySetSearchOptions(4, 1.3));
        Assert.True(configurable.TrySetVad(false));
    }

    [Fact]
    public void BlankPenalty_ShouldBePassedToTheEngine()
    {
        using var rig = Make();

        rig.Recognizer.BlankPenalty = 0.75f;

        Assert.Equal(0.75f, rig.Engine.BlankPenalty);
        Assert.Equal(0.75f, rig.Recognizer.BlankPenalty);
    }

    // ── identity and lifetime ───────────────────────────────────────────

    [Fact]
    public void Properties_ShouldDescribeTheStreamingGeometry()
    {
        using var rig = Make();
        IStreamingSpeechRecognizer recognizer = rig.Recognizer;

        Assert.Equal(16000, recognizer.SampleRate);
        Assert.Equal(N, recognizer.ChunkSamples);
        Assert.Equal(Frames, rig.Recognizer.FramesPerChunk);
        Assert.Equal(1280, rig.Recognizer.SamplesPerFrame);
        Assert.Null(recognizer.PartialText);
        Assert.NotNull(rig.Recognizer.Vocabulary);
        Assert.Equal(13087, rig.Recognizer.Config.BlankId);
    }

    [Fact]
    public void LastTokenCount_ShouldFollowTheInterfaceContract()
    {
        using var rig = Make();
        rig.Engine.Script = (_, _) => [(Id("▁a"), 0), (Id("▁b"), 0)];
        IStreamingSpeechRecognizer recognizer = rig.Recognizer;

        recognizer.ProcessAudio(TestAudio.Ramp(N));
        Assert.Equal(2, recognizer.LastTokenCount);

        recognizer.ProcessAudio(TestAudio.Ramp(10, N));       // no chunk completed: nothing produced by this call
        Assert.Equal(0, recognizer.LastTokenCount);
    }

    [Fact]
    public void Dispose_ShouldReleaseTheEngineAndTheGate()
    {
        var rig = Make(useVad: true);
        var gate = rig.Gate;

        rig.Dispose();
        rig.Dispose();                                    // idempotent

        Assert.True(rig.Engine.Disposed);
        Assert.True(gate!.Disposed);
        Assert.Throws<ObjectDisposedException>(() => rig.Recognizer.ProcessAudio(new float[10]));
        Assert.Throws<ObjectDisposedException>(() => rig.Recognizer.Flush());
        Assert.Throws<ObjectDisposedException>(() => rig.Recognizer.ResetStreamingState());
    }

    [Fact]
    public void Constructor_InconsistentEngine_ShouldThrow()
    {
        var engine = new FakeChunkEngine(chunkSamples: 8960, framesPerChunk: 8);   // 8960 / 8 = 1120: fine
        var bad = new FakeChunkEngine(chunkSamples: 8960, framesPerChunk: 6);      // not divisible

        using (new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value, () => new FakeGate(N, _ => true)))
        {
        }
        Assert.Throws<ArgumentException>(() =>
            new NemotronRnntRecognizer(bad, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value, () => new FakeGate(N, _ => true)));
        Assert.True(bad.Disposed);                         // a failed constructor does not leak the engine it was given
    }

    [Fact]
    public void LargeBatch_ShouldNotLeaveAHugeBufferBehind()
    {
        using var rig = Make();
        var baseline = N;                                              // chunk + look-ahead (0) is the buffer's starting size
        Assert.Equal(baseline, rig.Recognizer.BufferCapacity);

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(200 * N + 5));     // a whole recording in one call

        // the call grew the buffer to hold 200 chunks (about 9 MB); once they are consumed it is cut back to a few chunks
        Assert.Equal(200, rig.Engine.Calls.Count);
        Assert.Equal(5, rig.Recognizer.BufferedSamples);
        Assert.True(rig.Recognizer.BufferCapacity <= 4 * baseline, $"buffer capacity {rig.Recognizer.BufferCapacity} floats after the batch");

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(10, 200 * N + 5));
        Assert.Equal(15, rig.Recognizer.BufferedSamples);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N - 15, 200 * N + 15));
        Assert.Equal(201, rig.Engine.Calls.Count);
        Assert.True(rig.Recognizer.BufferCapacity <= 4 * baseline);
    }

    [Fact]
    public void Flush_AfterALargeBatch_ShouldAlsoCutTheBufferBack()
    {
        using var rig = Make();

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(100 * N + 7));
        rig.Recognizer.Flush();

        Assert.Equal(0, rig.Recognizer.BufferedSamples);
        Assert.True(rig.Recognizer.BufferCapacity <= 4 * N, $"buffer capacity {rig.Recognizer.BufferCapacity} floats after the flush");
    }

    // ── a failing engine ────────────────────────────────────────────────

    private static IEnumerable<(int Id, int Frame)> OneTokenThenFail(Exception error)
    {
        yield return (Id("▁d"), 3);   // the engine had already written a token when it failed
        throw error;
    }

    [Fact]
    public void ProcessAudio_WhenTheEngineThrowsOnAChunkOfABatch_ShouldKeepTheCountersConsistentAndRefuseFurtherAudio()
    {
        using var rig = Make();
        var failure = new InvalidOperationException("device lost");
        rig.Engine.Script = (chunk, _) => chunk == 2 ? OneTokenThenFail(failure) : [(Id("▁c"), chunk)];
        var audio = TestAudio.Ramp(5 * N + 100);

        var thrown = Assert.Throws<InvalidOperationException>(() => rig.Recognizer.ProcessAudio(audio));

        Assert.Same(failure, thrown);
        Assert.True(rig.Recognizer.IsFaulted);
        // chunks 0 and 1 went through and are out of the buffer, the failed chunk 2 was not counted and waits with the rest
        Assert.Equal(2, rig.Recognizer.ChunksConsumed);
        Assert.Equal(3 * N + 100, rig.Recognizer.BufferedSamples);
        Assert.Equal([0, 1], rig.Recognizer.LastChunks.Select(c => c.Index));
        Assert.Equal(2, rig.Recognizer.LastTokenCount);                          // the half-decoded token of chunk 2 left nothing behind
        Assert.Equal([Id("▁c"), Id("▁c")], rig.Recognizer.LastTokens.Select(t => t.Id));

        var again = Assert.Throws<InvalidOperationException>(() => rig.Recognizer.ProcessAudio(TestAudio.Ramp(10)));
        Assert.Same(failure, again.InnerException);
        Assert.Contains(nameof(NemotronRnntRecognizer.ResetStreamingState), again.Message);
        Assert.Contains("device lost", again.Message);
        Assert.Throws<InvalidOperationException>(() => rig.Recognizer.Flush());
        Assert.Throws<InvalidOperationException>(() => rig.Recognizer.ProcessAudio(new float[3 * N].AsSpan()));
        Assert.Equal(3, rig.Engine.Calls.Count);                                 // nothing reached the engine after the failure
        Assert.Equal(2, rig.Recognizer.ChunksConsumed);                          // and the refused calls changed nothing
        Assert.Equal(3 * N + 100, rig.Recognizer.BufferedSamples);
    }

    [Fact]
    public void ResetStreamingState_ShouldRecoverARecognizerThatFailed()
    {
        using var rig = Make();
        var calls = 0;
        rig.Engine.Script = (chunk, _) => ++calls == 3 ? OneTokenThenFail(new IOException("disk")) : [(Id("▁c"), 1)];
        var audio = TestAudio.Ramp(4 * N + 11);
        Assert.Throws<IOException>(() => rig.Recognizer.ProcessAudio(audio));
        Assert.True(rig.Recognizer.IsFaulted);

        rig.Recognizer.ResetStreamingState();

        Assert.False(rig.Recognizer.IsFaulted);
        Assert.Equal(0, rig.Recognizer.ChunksConsumed);
        Assert.Equal(0, rig.Recognizer.BufferedSamples);
        Assert.Equal(0, rig.Recognizer.LastTokenCount);
        Assert.Empty(rig.Recognizer.LastChunks);
        var text = rig.Recognizer.ProcessAudio(audio);                           // the same audio, now from chunk 0 again
        Assert.Equal(" c c c c", text);
        Assert.Equal([0, 1, 2, 3], rig.Recognizer.LastChunks.Select(c => c.Index));
        Assert.Equal(11, rig.Recognizer.BufferedSamples);
        Assert.Equal(" c", rig.Recognizer.Flush());
        Assert.Equal(5, rig.Recognizer.ChunksConsumed);
    }

    [Fact]
    public void Flush_WhenTheEngineThrows_ShouldFaultTheRecognizerToo()
    {
        using var rig = Make();
        rig.Engine.Script = (chunk, _) => chunk == 2 ? throw new InvalidOperationException("boom") : [];
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(2 * N + 100));                // chunks 0 and 1, 100 samples wait for the flush

        Assert.Throws<InvalidOperationException>(() => rig.Recognizer.Flush());

        Assert.True(rig.Recognizer.IsFaulted);
        Assert.Equal(2, rig.Recognizer.ChunksConsumed);
        Assert.Throws<InvalidOperationException>(() => rig.Recognizer.Flush());
        rig.Recognizer.ResetStreamingState();
        Assert.False(rig.Recognizer.IsFaulted);
    }

    [Fact]
    public void SilenceGate_WhenItThrows_ShouldFaultTheRecognizerLikeTheEngine()
    {
        var failure = new IOException("silero");
        using var rig = Make(useVad: true, speech: index => index == 3 ? throw failure : true);

        var thrown = Assert.Throws<IOException>(() => rig.Recognizer.ProcessAudio(TestAudio.Ramp(5 * N)));

        Assert.Same(failure, thrown);
        Assert.True(rig.Recognizer.IsFaulted);
        Assert.Equal(3, rig.Recognizer.ChunksConsumed);                          // chunk 3 was being scored, not consumed
        Assert.Equal(2 * N, rig.Recognizer.BufferedSamples);
        Assert.Throws<InvalidOperationException>(() => rig.Recognizer.ProcessAudio(TestAudio.Ramp(N)));
    }

    // ── non-finite input ────────────────────────────────────────────────

    [Fact]
    public void ProcessAudio_NonFiniteSamples_ShouldReachTheEngineAsSilenceAndBeCounted()
    {
        using var rig = Make();
        var audio = TestAudio.Ramp(2 * N);
        audio[5] = float.NaN;
        audio[N + 7] = float.PositiveInfinity;
        audio[2 * N - 1] = float.NegativeInfinity;
        var untouched = TestAudio.Ramp(2 * N);

        rig.Recognizer.ProcessAudio(audio);

        Assert.Equal(3, rig.Recognizer.NonFiniteSamples);
        Assert.Equal(0f, rig.Engine.Calls[0].Chunk[5]);
        Assert.Equal(0f, rig.Engine.Calls[1].Chunk[7]);
        Assert.Equal(0f, rig.Engine.Calls[1].Chunk[N - 1]);
        // every other sample is the caller's, bit for bit
        Assert.Equal(untouched[6], rig.Engine.Calls[0].Chunk[6]);
        Assert.All(rig.Engine.Calls, call => Assert.All(call.Chunk, s => Assert.True(float.IsFinite(s))));
        Assert.True(float.IsNaN(audio[5]));                                      // the caller's array is not modified
    }

    [Fact]
    public void Flush_NonFiniteSamplesInTheRemainder_ShouldBePaddedAsSilenceToo()
    {
        using var rig = Make();
        rig.Recognizer.ProcessAudio([1f, float.NaN, 2f]);

        rig.Recognizer.Flush();

        var chunk = Assert.Single(rig.Engine.Calls).Chunk;
        Assert.Equal([1f, 0f, 2f, 0f], chunk.Take(4));
        Assert.Equal(1, rig.Recognizer.NonFiniteSamples);
    }

    [Fact]
    public void ProcessAudio_WithBadArguments_ShouldNotFaultTheRecognizer()
    {
        using var rig = Make();

        Assert.Throws<ArgumentNullException>(() => rig.Recognizer.ProcessAudio((float[])null!));

        Assert.False(rig.Recognizer.IsFaulted);
        Assert.Equal("", rig.Recognizer.ProcessAudio(TestAudio.Ramp(N)));
    }
}
