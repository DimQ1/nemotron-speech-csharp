using System.Text;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The pause-state policy of the recognizer (<see cref="NemotronPauseStateOptions"/>) over a two-branch fake engine and a fake speech
/// gate: when a fresh branch is started, what it hears first, which branch wins, how the window's text is reported, the closing marks
/// of the sentence before, the stream clock, batching independence, flush and reset. No model needed.
/// </summary>
public sealed class Unit_NemotronPauseStateTests
{
    private const int N = 8960;       // 560 ms chunk
    private const int F = 7;

    private static readonly Lazy<NemotronVocabulary> VocabularyOf = new(() =>
        NemotronVocabulary.FromPieces(NemotronTestEnvironment.ReadVocabularyLines(), 13087, 13088));

    private static int Id(string piece)
    {
        var id = Array.IndexOf(NemotronTestEnvironment.ReadVocabularyLines(), piece);
        return id >= 0 ? id : throw new InvalidOperationException($"'{piece}' is not in the vocabulary");
    }

    // 1.1 s = 2 silent chunks make a pause, 3 chunks are decided on, 1.1 s = 2 chunks of preroll, a lead of one letter token wins
    private static NemotronPauseStateOptions Dual(int decisionChunks = 3) =>
        new() { Policy = NemotronPauseState.DualBranch, PauseSeconds = 1.1, DecisionChunks = decisionChunks, PrerollSeconds = 1.1, FreshLead = 1 };

    private sealed class Rig : IDisposable
    {
        public required NemotronRnntRecognizer Recognizer { get; init; }
        public required FakeBranchingEngine Engine { get; init; }
        public required List<FakeGate> Gates { get; init; }
        public List<NemotronPauseDecision> Decisions { get; } = new();
        public void Dispose() => Recognizer.Dispose();

        /// <summary>Feeds chunk-sized pieces of the ramp from <paramref name="firstChunk"/> on and returns each call's text.</summary>
        public List<string?> FeedChunks(int firstChunk, int count)
        {
            var texts = new List<string?>();
            for (var k = firstChunk; k < firstChunk + count; k++)
                texts.Add(Recognizer.ProcessAudio(TestAudio.Ramp(N, k * N)));
            return texts;
        }
    }

    private static Rig Make(NemotronPauseStateOptions? pause, Func<int, bool> speech, bool useVad = false, int closing = 0,
        Func<IChunkSpeechGate>? gateFactory = null)
    {
        var engine = new FakeBranchingEngine(N, F);
        var gates = new List<FakeGate>();
        gateFactory ??= () =>
        {
            var gate = new FakeGate(N, speech);
            gates.Add(gate);
            return gate;
        };
        var options = new NemotronRecognizerOptions { PauseState = pause ?? NemotronPauseStateOptions.Parity, FlushClosingChunks = closing };
        var recognizer = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value, gateFactory,
            useVad, langId: null, options);
        var rig = new Rig { Recognizer = recognizer, Engine = engine, Gates = gates };
        recognizer.PauseDecided += rig.Decisions.Add;
        return rig;
    }

    // speech in chunks 0-2, a pause of 3 chunks, speech again from chunk 6
    private static bool NewSentence(int chunk) => chunk is < 3 or >= 6;

    // keep (lineage 0) writes the closers of the first sentence and then loses words; a fresh state hears the new sentence
    private static Func<int, int, int, IEnumerable<(int Id, int Frame)>> KeepLosesWords() => (lineage, chunk, _) => (lineage, chunk) switch
    {
        (0, 0) => [(Id("▁the"), 2)],
        (0, 6) => [(Id("."), 0), (Id("▁"), 0), (Id("<en-US>"), 0)],
        (0, 7) => [(Id("▁are"), 3)],
        (> 0, 6) => [(Id("▁you"), 2)],
        (> 0, 7) => [(Id("▁are"), 1)],
        (> 0, 8) => [(Id("▁so"), 1), (Id("▁r"), 5), (Id("u"), 6), (Id("de"), 6)],
        (> 0, 9) => [(Id("▁there"), 3)],
        _ => [],
    };

    private static string AllText(IEnumerable<string?> texts) => string.Concat(texts.Where(t => t is not null));

    // ── the default ─────────────────────────────────────────────────────

    [Fact]
    public void Default_ShouldKeepTheStateAndNeverScoreOrForkWithoutTheSilenceDrop()
    {
        using var rig = Make(pause: null, NewSentence);
        rig.Engine.Script = KeepLosesWords();

        var texts = rig.FeedChunks(0, 10);

        Assert.Equal(NemotronPauseState.Keep, rig.Recognizer.PauseState);
        Assert.Empty(rig.Gates);                                    // no gate is created, nothing is scored
        Assert.Equal(0, rig.Engine.Selections);
        Assert.Empty(rig.Engine.Resets);
        Assert.All(rig.Engine.Calls, c => Assert.Equal((0, 0), (c.Branch, c.Lineage)));
        Assert.Equal(" the. <en-US> are", AllText(texts));
        Assert.Equal(0, rig.Recognizer.PauseForks);
    }

    // ── fork and decision ───────────────────────────────────────────────

    [Fact]
    public void DualBranch_NewSentenceAfterAPause_ShouldContinueWithTheFreshBranchAndKeepTheClosersOfTheSentenceBefore()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = KeepLosesWords();

        var texts = rig.FeedChunks(0, 10);

        Assert.Equal([" the", "", "", "", "", "", "", "", ". <en-US> you are so rude", " there"], texts);
        var decision = Assert.Single(rig.Decisions);
        Assert.Equal((6, 3, 3, 1, 6, true), (decision.FirstChunk, decision.Chunks, decision.SilentChunks, decision.KeepLetters, decision.FreshLetters, decision.FreshWon));
        Assert.Equal(". <en-US> are", decision.KeepText);
        Assert.Equal(" you are so rude", decision.FreshText);
        Assert.Equal(1, rig.Recognizer.PauseForks);
        Assert.Equal(1, rig.Recognizer.FreshBranchWins);
        Assert.Equal(10, rig.Recognizer.ChunksConsumed);           // the clock ran on
    }

    [Fact]
    public void DualBranch_InAKeepLanguage_ShouldKeepTheStateThroughThePause()
    {
        using var rig = Make(Dual() with { KeepLanguages = ["ru"] }, NewSentence);
        rig.Engine.Script = KeepLosesWords();
        Assert.True(rig.Recognizer.TrySetLanguage("ru"));

        var texts = rig.FeedChunks(0, 10);

        Assert.Equal(" the. <en-US> are", AllText(texts));
        Assert.Empty(rig.Decisions);
        Assert.Equal(0, rig.Recognizer.PauseForks);
    }

    [Fact]
    public void DualBranch_LanguageSwitchedOutOfAKeepLanguageDuringThePause_ShouldForkAtThatPause()
    {
        using var rig = Make(Dual() with { KeepLanguages = ["ru"] }, NewSentence);
        rig.Engine.Script = KeepLosesWords();
        Assert.True(rig.Recognizer.TrySetLanguage("ru"));

        rig.FeedChunks(0, 6);
        Assert.True(rig.Recognizer.TrySetLanguage("auto"));
        rig.FeedChunks(6, 4);

        Assert.True(Assert.Single(rig.Decisions).FreshWon);
    }

    [Fact]
    public void DualBranch_TheFreshBranch_ShouldBeResetAndHearTheLastChunksOfThePauseFirst()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = KeepLosesWords();

        rig.FeedChunks(0, 10);

        var reset = Assert.Single(rig.Engine.Resets);
        Assert.Equal(1, reset.Branch);
        var fresh = rig.Engine.Calls.Where(c => c.Lineage == reset.Lineage).ToList();
        Assert.Equal([4, 5, 6, 7, 8, 9], fresh.Select(c => c.ChunkIndex));      // preroll 4, 5 (2 of the 3 silent chunks), then the stream
        Assert.Equal(fresh.Select(c => (float)c.ChunkIndex * N), fresh.Select(c => c.FirstSample));   // each with its own audio
        var keep = rig.Engine.Calls.Where(c => c.Lineage == 0).ToList();
        Assert.Equal(Enumerable.Range(0, 9), keep.Select(c => c.ChunkIndex));   // the kept state decoded up to the end of the window
        Assert.Equal(1, rig.Engine.ActiveBranch);
    }

    [Fact]
    public void DualBranch_TheWindow_ShouldBeReportedByTheCallThatEndsItWithStreamPositions()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = KeepLosesWords();
        rig.FeedChunks(0, 6);

        Assert.Equal("", rig.Recognizer.ProcessAudio(TestAudio.Ramp(N, 6 * N)));   // encoded, held
        Assert.Empty(rig.Recognizer.LastChunks);
        Assert.Empty(rig.Recognizer.LastTokens);
        rig.FeedChunks(7, 1);
        Assert.Empty(rig.Recognizer.LastChunks);

        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N, 8 * N));

        var chunks = rig.Recognizer.LastChunks;
        Assert.Equal([6, 7, 8], chunks.Select(c => c.Index));
        Assert.Equal([3 + 1, 1, 4], chunks.Select(c => c.TokenCount));                  // closers + "you", "are", "so r u de"
        var tokens = rig.Recognizer.LastTokens;
        Assert.Equal(chunks.Sum(c => c.TokenCount), tokens.Count);
        Assert.Equal(rig.Recognizer.LastTokenCount, tokens.Count);
        Assert.Equal([42L, 42, 42, 44, 50, 57, 61, 62, 62], tokens.Select(t => t.GlobalFrame));
        Assert.Equal([0, 1, 2, 0, 0, 0, 0, 0, 1], tokens.Select(t => t.Symbol));
        Assert.All(tokens, t => Assert.Equal(t.Chunk * F + t.Frame, t.GlobalFrame));
    }

    [Fact]
    public void DualBranch_WhenTheKeptStateWritesMoreLetters_ShouldKeepItAndDropTheFreshBranch()
    {
        using var rig = Make(Dual(), NewSentence);
        // a hesitation inside a sentence: the kept state carries on, a fresh state loses the start
        rig.Engine.Script = (lineage, chunk, _) => (lineage, chunk) switch
        {
            (0, 0) => [(Id("▁the"), 2)],
            (0, 6) => [(Id("▁be"), 2), (Id("ga"), 3), (Id("n"), 4)],
            (0, 7) => [(Id("▁to"), 1)],
            (0, 9) => [(Id("▁there"), 2)],
            (> 0, 7) => [(Id("▁to"), 1)],
            (> 0, 9) => [(Id("▁go"), 2)],
            _ => [],
        };

        var texts = rig.FeedChunks(0, 10);

        Assert.Equal(" the began to there", AllText(texts));
        Assert.False(Assert.Single(rig.Decisions).FreshWon);
        Assert.Equal(0, rig.Engine.ActiveBranch);
        Assert.Equal(9, rig.Engine.Calls.Last().ChunkIndex);
        Assert.Equal(0, rig.Engine.Calls.Last().Lineage);          // the kept state continues the stream
        Assert.Equal(0, rig.Recognizer.FreshBranchWins);
    }

    [Fact]
    public void DualBranch_OnATie_ShouldKeepTheState()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = (lineage, chunk, _) => (lineage, chunk) switch
        {
            (0, 0) => [(Id("▁the"), 2)],
            (0, 6) => [(Id("▁are"), 2)],
            (> 0, 6) => [(Id("▁you"), 2)],
            _ => [],
        };

        var texts = rig.FeedChunks(0, 10);

        Assert.Equal(" the are", AllText(texts));
        Assert.False(Assert.Single(rig.Decisions).FreshWon);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void DualBranch_FreshLead_ShouldBeTheLetterMarginTheFreshBranchNeeds(int lead, bool freshWins)
    {
        using var rig = Make(Dual() with { FreshLead = lead }, NewSentence);
        rig.Engine.Script = (lineage, chunk, _) => (lineage, chunk) switch
        {
            (0, 0) => [(Id("▁the"), 2)],
            (0, 6) => [(Id("▁are"), 2)],
            (> 0, 6) => [(Id("▁you"), 2), (Id("▁are"), 4)],   // one letter token more
            _ => [],
        };

        rig.FeedChunks(0, 10);

        Assert.Equal(freshWins, Assert.Single(rig.Decisions).FreshWon);
    }

    [Fact]
    public void DualBranch_ACloserEmittedAfterTheFreshBranchsFirstWord_ShouldMoveOntoThatWordsFrame()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = (lineage, chunk, _) => (lineage, chunk) switch
        {
            (0, 0) => [(Id("▁the"), 2)],
            (0, 6) => [(Id("."), 5), (Id("▁"), 6)],
            (> 0, 6) => [(Id("▁you"), 1), (Id("▁are"), 4)],
            _ => [],
        };

        var texts = rig.FeedChunks(0, 9);

        Assert.Equal(" the. you are", AllText(texts));            // the bare marker after the mark belongs to the next word: left out
        var tokens = rig.Recognizer.LastTokens;
        Assert.Equal([43L, 43, 46], tokens.Select(t => t.GlobalFrame));
        Assert.Equal([0, 1, 0], tokens.Select(t => t.Symbol));
        Assert.Equal((6, 1), (tokens[0].Chunk, tokens[0].Frame));
    }

    [Fact]
    public void Fresh_ShouldAlwaysContinueWithTheFreshBranch()
    {
        using var rig = Make(Dual() with { Policy = NemotronPauseState.Fresh }, NewSentence);
        rig.Engine.Script = (lineage, chunk, _) => (lineage, chunk) switch
        {
            (0, 0) => [(Id("▁the"), 2)],
            (0, 6) => [(Id("?"), 1), (Id("▁be"), 2), (Id("ga"), 3), (Id("n"), 4)],
            (> 0, 7) => [(Id("▁to"), 1)],
            _ => [],
        };

        var texts = rig.FeedChunks(0, 10);

        Assert.Equal(" the? to", AllText(texts));
        Assert.True(Assert.Single(rig.Decisions).FreshWon);
    }

    // ── when the policy starts ──────────────────────────────────────────

    [Fact]
    public void DualBranch_APauseShorterThanPauseSeconds_ShouldNotFork()
    {
        using var rig = Make(Dual(), chunk => chunk != 3);   // one silent chunk only
        rig.Engine.Script = KeepLosesWords();

        rig.FeedChunks(0, 10);

        Assert.Equal(0, rig.Recognizer.PauseForks);
        Assert.Empty(rig.Engine.Resets);
    }

    [Fact]
    public void DualBranch_SpeechAfterSilenceAtTheStartOfTheStream_ShouldNotFork()
    {
        using var rig = Make(Dual(), chunk => chunk >= 4);   // the state has not heard speech yet: it is as good as fresh
        rig.Engine.Script = KeepLosesWords();

        rig.FeedChunks(0, 10);

        Assert.Equal(0, rig.Recognizer.PauseForks);
    }

    [Fact]
    public void DualBranch_EveryPause_ShouldGetItsOwnDecisionAndAlternateTheBranches()
    {
        using var rig = Make(Dual(), chunk => chunk is < 3 or (>= 6 and < 9) or >= 12);
        // a state that has decoded six chunks goes stale and writes nothing: the fresh branch wins both times
        rig.Engine.Script = (_, _, since) => since < 6 ? [(Id("▁are"), 2), (Id("▁you"), 4)] : [];

        rig.FeedChunks(0, 16);

        Assert.Equal(2, rig.Recognizer.PauseForks);
        Assert.Equal([6, 12], rig.Decisions.Select(d => d.FirstChunk));
        Assert.Equal([(1, 1), (0, 2)], rig.Engine.Resets);         // the branch that lost is the next fresh one
    }

    [Fact]
    public void DualBranch_WithTheSilenceDrop_ShouldShareItsGateAndScoreEveryChunkOnce()
    {
        using var rig = Make(Dual(), NewSentence, useVad: true);
        rig.Engine.Script = KeepLosesWords();

        rig.FeedChunks(0, 10);

        var gate = Assert.Single(rig.Gates);
        Assert.Equal(Enumerable.Range(0, 10), gate.Scored);
        Assert.Equal(1, rig.Recognizer.PauseForks);
    }

    [Fact]
    public void DualBranch_ALongPauseInsideTheWindow_ShouldEndTheWindowBeforeTheFirstDroppedChunk()
    {
        // speech at chunk 6 only, then silence: the 6th silent chunk (12) is dropped by the GenAI rule
        using var rig = Make(Dual(decisionChunks: 9), chunk => chunk is < 3 or 6, useVad: true);
        rig.Engine.Script = KeepLosesWords();

        rig.FeedChunks(0, 12);
        Assert.Empty(rig.Decisions);
        rig.FeedChunks(12, 1);

        var decision = Assert.Single(rig.Decisions);
        Assert.Equal(6, decision.Chunks);                           // chunks 6 to 11
        Assert.Equal([6, 7, 8, 9, 10, 11, 12], rig.Recognizer.LastChunks.Select(c => c.Index));
        Assert.True(rig.Recognizer.LastChunks[^1].Dropped);
        Assert.Equal(1, rig.Recognizer.ChunksDropped);
    }

    [Fact]
    public void DualBranch_WithoutAGate_ShouldStayOffAndKeepTheState()
    {
        using var rig = Make(Dual(), NewSentence, gateFactory: () => throw new FileNotFoundException("no silero_vad.onnx"));
        rig.Engine.Script = KeepLosesWords();

        var texts = rig.FeedChunks(0, 10);

        Assert.Equal(" the. <en-US> are", AllText(texts));
        Assert.Equal(0, rig.Recognizer.PauseForks);
    }

    [Fact]
    public void DualBranch_OnAnEngineWithOneBranch_ShouldBeRefusedAndDisposeTheEngine()
    {
        var engine = new FakeChunkEngine(N, F);
        var options = new NemotronRecognizerOptions { PauseState = Dual() };

        Assert.Throws<ArgumentException>(() => new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value,
            () => new FakeGate(N, _ => true), options: options));
        Assert.True(engine.Disposed);
    }

    // ── batching, flush, reset ──────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3200)]
    [InlineData(N + 1)]
    [InlineData(3 * N - 7)]
    [InlineData(100000)]
    public void DualBranch_AnyBatchSize_ShouldGiveTheSameTextAndTokens(int batch)
    {
        var audio = TestAudio.Ramp(10 * N + 500);
        string Run(int size, out List<NemotronToken> tokens)
        {
            using var rig = Make(Dual(), NewSentence);
            rig.Engine.Script = KeepLosesWords();
            var text = new StringBuilder();
            tokens = new List<NemotronToken>();
            foreach (var piece in TestAudio.Split(audio, size))
            {
                text.Append(rig.Recognizer.ProcessAudio(piece));
                tokens.AddRange(rig.Recognizer.LastTokens);
            }
            text.Append(rig.Recognizer.Flush());
            tokens.AddRange(rig.Recognizer.LastTokens);
            return text.ToString();
        }

        var expected = Run(N, out var expectedTokens);
        var actual = Run(batch, out var actualTokens);

        Assert.Equal(expected, actual);
        Assert.Equal(expectedTokens, actualTokens);
    }

    [Fact]
    public void Flush_InsideTheWindow_ShouldDecideOnWhatBothBranchesHeardAndReturnTheText()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = KeepLosesWords();
        rig.FeedChunks(0, 7);
        rig.Recognizer.ProcessAudio(TestAudio.Ramp(N / 2, 7 * N));

        var tail = rig.Recognizer.Flush();

        Assert.Equal(". <en-US> you are", tail);
        var decision = Assert.Single(rig.Decisions);
        Assert.Equal(2, decision.Chunks);                           // chunk 6 and the padded chunk 7
        Assert.Equal([6, 7], rig.Recognizer.LastChunks.Select(c => c.Index));
        Assert.Null(rig.Recognizer.Flush());
    }

    [Fact]
    public void ResetStreamingState_InsideTheWindow_ShouldForgetItAndStartANewStream()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = KeepLosesWords();
        rig.FeedChunks(0, 7);

        rig.Recognizer.ResetStreamingState();
        var text = rig.Recognizer.ProcessAudio(TestAudio.Ramp(N));

        Assert.Empty(rig.Decisions);
        Assert.Equal(0, rig.Recognizer.PauseForks);
        Assert.Equal(1, rig.Recognizer.ChunksConsumed);
        Assert.Equal([0], rig.Recognizer.LastChunks.Select(c => c.Index));
        Assert.Equal(rig.Engine.Resets[^1].Lineage, rig.Engine.Calls[^1].Lineage);
        Assert.Equal("", text);                                     // a new lineage: the script has nothing for chunk 0
    }

    [Fact]
    public void ResetModelState_ShouldSettleAPendingDecisionAndKeepTheClock()
    {
        using var rig = Make(Dual(), NewSentence);
        rig.Engine.Script = KeepLosesWords();
        rig.FeedChunks(0, 8);

        var settled = rig.Recognizer.ResetModelState();

        Assert.Equal(". <en-US> you are", settled);
        Assert.Equal([6, 7], rig.Recognizer.LastChunks.Select(c => c.Index));
        Assert.Equal(8, rig.Recognizer.ChunksConsumed);
        var lineage = rig.Engine.Resets[^1].Lineage;
        rig.FeedChunks(8, 1);
        Assert.Equal((lineage, 8), (rig.Engine.Calls[^1].Lineage, rig.Engine.Calls[^1].ChunkIndex));
        Assert.Null(rig.Recognizer.ResetModelState());              // nothing pending: like a call without audio
    }
}
