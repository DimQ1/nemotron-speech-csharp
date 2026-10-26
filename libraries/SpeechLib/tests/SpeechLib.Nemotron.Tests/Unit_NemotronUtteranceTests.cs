using SpeechLib.Audio;
using SpeechLib.Models;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The utterance and word-time layer over a fake chunk engine: endpointing and commit rules, tag handling, word building,
/// the clock (dropped chunks, flush padding), batching independence and reset. No model needed.
/// </summary>
public sealed class Unit_NemotronUtteranceTests
{
    private const int N = 8960;       // 560 ms chunk
    private const int F = 7;          // frames per chunk
    private const double Frame = 0.08;

    private static readonly Lazy<NemotronVocabulary> VocabularyOf = new(() =>
        NemotronVocabulary.FromPieces(NemotronTestEnvironment.ReadVocabularyLines(), 13087, 13088));

    private static int Id(string piece)
    {
        var id = Array.IndexOf(NemotronTestEnvironment.ReadVocabularyLines(), piece);
        return id >= 0 ? id : throw new InvalidOperationException($"'{piece}' is not in the vocabulary");
    }

    /// <summary>Raw frame times, no snapping: word start = frame * 80 ms.</summary>
    private static NemotronUtteranceOptions RawOptions(double eou = NemotronUtteranceOptions.DefaultStopHistoryEouSeconds, double cap = 30,
        bool keepTag = true) =>
        new()
        {
            StopHistoryEouSeconds = eou,
            MaxUtteranceSeconds = cap,
            KeepLanguageTagInFinal = keepTag,
            WordTiming = NemotronWordTimingOptions.Raw,
        };

    /// <summary>The shipped calibration without snapping.</summary>
    private static NemotronUtteranceOptions CalibratedOptions() =>
        new() { WordTiming = NemotronWordTimingOptions.Default with { Snap = null } };

    private sealed class Rig : IDisposable
    {
        public required NemotronUtteranceRecognizer Recognizer { get; init; }
        public required FakeChunkEngine Engine { get; init; }
        public List<StreamingResult> Results { get; } = new();
        public void Dispose() => Recognizer.Dispose();

        public StreamingResult Feed(float[] audio)
        {
            var result = Recognizer.ProcessUtterance(audio);
            Results.Add(result);
            return result;
        }

        /// <summary>Feeds <paramref name="count"/> chunk-sized pieces of audio and returns each call's result.</summary>
        public List<StreamingResult> FeedChunks(int count, float[]? audio = null)
        {
            var results = new List<StreamingResult>();
            for (var k = 0; k < count; k++)
                results.Add(Feed(audio is null ? new float[Engine.ChunkSamples] : audio[(k * Engine.ChunkSamples)..((k + 1) * Engine.ChunkSamples)]));
            return results;
        }
    }

    private static Rig Make(NemotronUtteranceOptions? options = null, int chunk = N, int frames = F, string? langId = null, bool useVad = false,
        Func<int, bool>? speech = null, Func<NemotronRnntRecognizer>? createOfflineEngine = null, params (int Frame, string Piece)[] tokens)
    {
        var (recognizer, engine) = NewEngine(chunk, frames, langId, useVad, speech, tokens);
        return new Rig { Recognizer = new NemotronUtteranceRecognizer(recognizer, options ?? RawOptions(), createOfflineEngine), Engine = engine };
    }

    private static (NemotronRnntRecognizer Recognizer, FakeChunkEngine Engine) NewEngine(int chunk, int frames, string? langId, bool useVad,
        Func<int, bool>? speech, (int Frame, string Piece)[] tokens)
    {
        var variant = chunk == 17920 ? "int4-c112" : "int4-c056";
        var engine = new FakeChunkEngine(chunk, frames);
        if (tokens.Length > 0)
            engine.Script = Script(frames, tokens);
        speech ??= _ => true;
        var recognizer = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig(variant), VocabularyOf.Value,
            () => new FakeGate(chunk, speech), useVad, langId);
        return (recognizer, engine);
    }

    /// <summary>A chunk script from absolute frames: every token goes to the chunk its frame lies in.</summary>
    private static Func<int, float[], IEnumerable<(int Id, int Frame)>> Script(int framesPerChunk, params (int Frame, string Piece)[] tokens) =>
        (chunk, _) => tokens.Where(t => t.Frame / framesPerChunk == chunk).Select(t => (Id(t.Piece), t.Frame % framesPerChunk));

    private static int WhitespaceWords(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"<\w{2,3}(-\w{2,4})?>", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ── tags and text ───────────────────────────────────────────────────

    [Fact]
    public void LanguageTag_AfterLetters_ShouldEndTheUtteranceAndStayOutOfThePartialAndTheWords()
    {
        using var rig = Make(RawOptions(), tokens: [(2, "▁the"), (3, "▁there"), (5, "."), (5, "▁"), (5, "<en-US>")]);

        var result = rig.Feed(new float[N]);

        Assert.Equal("the there. <en-US>", result.Final);           // the closing tag stays in the Final text
        Assert.Equal("", result.Partial);
        Assert.Equal("", result.Stable);
        Assert.Empty(result.PartialWords!);
        Assert.Equal(["the", "there."], result.FinalWords!.Select(w => w.Word));
        Assert.Equal(0.16, result.FinalWords![0].StartSeconds, 6);
        Assert.Equal(0.24, result.FinalWords[0].EndSeconds, 6);     // ends where the next word starts
        Assert.Equal(0.24, result.FinalWords[1].StartSeconds, 6);
        Assert.Equal(0.32, result.FinalWords[1].EndSeconds, 6);     // the "." does not extend the word
        Assert.Equal("en-US", rig.Recognizer.LastLanguageTag);
    }

    [Fact]
    public void LanguageTag_WithKeepLanguageTagInFinalFalse_ShouldGiveATagFreeFinal()
    {
        using var rig = Make(RawOptions(keepTag: false), tokens: [(2, "▁the"), (3, "▁there"), (5, "."), (5, "▁"), (5, "<en-US>")]);

        var result = rig.Feed(new float[N]);

        Assert.Equal("the there.", result.Final);
        Assert.Equal(2, result.FinalWords!.Count);
    }

    [Fact]
    public void Partial_BeforeAnEnd_ShouldCarryTheCommittedTextAndStableShouldEqualIt()
    {
        using var rig = Make(RawOptions(), tokens: [(2, "▁prin"), (3, "ci"), (4, "p"), (5, "al")]);

        var first = rig.Feed(new float[N]);

        Assert.Null(first.Final);
        Assert.Equal("principal", first.Partial);
        Assert.Equal("principal", first.Stable);
        var word = Assert.Single(first.PartialWords!);
        Assert.Equal("principal", word.Word);
        Assert.Equal(0.16, word.StartSeconds, 6);
        Assert.Null(rig.Recognizer.PartialText);               // nothing is provisional: the open utterance is the Partial
        Assert.Equal(4, rig.Recognizer.LastTokenCount);
        Assert.Equal(4, word.Tokens!.Count);
    }

    [Fact]
    public void Text_OfAllFinalsAndThePartial_ShouldEqualThePlainTextWithoutTags()
    {
        // two sentences with tags, a comma, an apostrophe, and a tail that is only flushed
        (int, string)[] tokens =
        [
            (1, "▁the"), (2, "▁there"), (3, ","), (4, "▁and"), (5, "▁has"), (6, "."), (6, "▁"), (6, "<en-US>"),
            (8, "▁the"), (9, "▁H"), (9, "ar"), (10, "'"), (10, "s"), (12, "?"), (12, "▁"), (12, "<en-US>"),
            (30, "▁be"), (31, "en"), (31, "."),
        ];
        using var rig = Make(RawOptions(), tokens: tokens);

        rig.FeedChunks(5);
        rig.Results.Add(rig.Recognizer.FlushUtterance());

        Assert.Equal(Normalize(PlainText(tokens)), Normalize(JoinedText(rig)));
        Assert.All(rig.Results, r => Assert.DoesNotContain("<", r.Partial));
    }

    [Fact]
    public void Text_WithAClosingMarkThatArrivesAfterTheSilenceCommit_ShouldJoinWithoutASpaceBeforeIt()
    {
        // "there" is committed by the silence rule in chunk 2; the model writes its "." and the tag only when "the and" begins
        (int, string)[] tokens = [(1, "▁there"), (22, "."), (22, "▁"), (22, "<en-US>"), (23, "▁the"), (24, "▁and"), (30, "."), (30, "▁"), (30, "<en-US>")];
        using var rig = Make(RawOptions(), tokens: tokens);

        rig.FeedChunks(7);
        rig.Results.Add(rig.Recognizer.FlushUtterance());

        var finals = rig.Results.Where(r => r.Final is not null).ToList();
        Assert.Equal(["there", ". <en-US> the and. <en-US>"], finals.Select(r => r.Final));
        Assert.Equal([false, true], finals.Select(r => r.FinalContinuesPrevious));      // the second starts with the mark
        Assert.Equal("there. <en-US> the and. <en-US>", JoinedText(rig));
        Assert.Equal(Normalize(PlainText(tokens)), Normalize(JoinedText(rig)));
        foreach (var result in rig.Results.Where(r => r.Final is not null))
            Assert.Equal(WhitespaceWords(result.Final!), result.FinalWords!.Count);
    }

    [Fact]
    public void ThePiecesOfOneWordMoreThanTheSilenceApart_ShouldBeJoinedWithoutASpace()
    {
        // "there" + (1.6 s of silence) + "s": the model emitted the end of the word late, a silence closed the utterance in between
        (int, string)[] tokens = [(1, "▁there"), (22, "s"), (23, "▁and"), (24, "▁"), (24, "<en-US>")];
        using var aligned = Make(RawOptions(), tokens: tokens);
        using var oneCall = Make(RawOptions(), tokens: tokens);

        aligned.FeedChunks(4);
        aligned.Results.Add(aligned.Recognizer.FlushUtterance());
        var oneResult = oneCall.Recognizer.ProcessUtterance(new float[4 * N]);

        var finals = aligned.Results.Where(r => r.Final is not null).ToList();
        Assert.Equal(["there", "s and <en-US>"], finals.Select(r => r.Final));
        Assert.Equal([false, true], finals.Select(r => r.FinalContinuesPrevious));
        Assert.Equal("theres and", Normalize(JoinedText(aligned)));
        Assert.Equal(Normalize(PlainText(tokens)), Normalize(JoinedText(aligned)));

        // both utterances end in one call: one Final, joined the same way, one timed word per whitespace word
        Assert.Equal("theres and <en-US>", oneResult.Final);
        Assert.False(oneResult.FinalContinuesPrevious);
        Assert.Equal(["theres", "and"], oneResult.FinalWords!.Select(w => w.Word));
        Assert.Equal(1 * Frame, oneResult.FinalWords![0].StartSeconds, 6);
        Assert.Equal(22 * Frame + Frame, oneResult.FinalWords[0].EndSeconds, 6);       // the end of the word is the end of "s" (end of its slot)
    }

    [Fact]
    public void AClosingMarkInTheSameCallAsTheCommit_ShouldJoinTheFinalAndTheWordsWithoutASpace()
    {
        (int, string)[] tokens = [(1, "▁there"), (22, "."), (22, "▁"), (22, "<en-US>"), (23, "▁the"), (24, "▁and"), (30, "."), (30, "▁"), (30, "<en-US>")];
        using var rig = Make(RawOptions(), tokens: tokens);

        var result = rig.Recognizer.ProcessUtterance(new float[5 * N]);

        Assert.Equal("there. <en-US> the and. <en-US>", result.Final);
        Assert.Equal(["there.", "the", "and."], result.FinalWords!.Select(w => w.Word));
        Assert.Equal(WhitespaceWords(result.Final!), result.FinalWords!.Count);
        Assert.Equal(1 * Frame, result.FinalWords![0].StartSeconds, 6);
        Assert.Equal(2 * Frame, result.FinalWords[0].EndSeconds, 6);                   // the mark did not extend "there"
    }

    private static string PlainText((int, string)[] tokens) => string.Concat(tokens.Select(t => VocabularyOf.Value.Decode(Id(t.Item2))));

    // the transcript the way a consumer assembles it: every Final joined with the continuation flag, then the last partial
    private static string JoinedText(Rig rig)
    {
        var text = "";
        foreach (var result in rig.Results.Where(r => r.Final is not null))
            text = UtteranceText.Join(text, result.Final, result.FinalContinuesPrevious);
        return UtteranceText.Join(text, rig.Results[^1].Partial);
    }

    private static string Normalize(string text) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text, @"<\w{2,3}(-\w{2,4})?>", " "), @"\s+", " ").Trim();

    [Fact]
    public void FinalWords_ShouldBeOnePerWhitespaceWordOfTheTagFreeFinal()
    {
        (int, string)[] tokens =
        [
            (1, "▁the"), (2, "▁there"), (3, ","), (4, "▁and"), (5, "▁has"), (6, "."), (6, "▁"), (6, "<en-US>"),
            (8, "▁the"), (9, "▁H"), (9, "ar"), (10, "'"), (10, "s"), (12, "?"), (12, "▁"), (12, "<en-US>"),
        ];
        using var rig = Make(RawOptions(), tokens: tokens);

        rig.FeedChunks(3);
        foreach (var result in rig.Results.Where(r => r.Final is not null))
            Assert.Equal(WhitespaceWords(result.Final!), result.FinalWords!.Count);
        Assert.Equal(2, rig.Results.Count(r => r.Final is not null));
    }

    [Fact]
    public void AMarkThatTheModelWritesAsAWordOfItsOwn_ShouldBeAttachedToThePreviousWordInTextAndWords()
    {
        // "the there" + word marker + "." (a detached mark) + word marker + "," + a real word
        using var rig = Make(RawOptions(), tokens: [(1, "▁the"), (2, "▁there"), (3, "▁"), (3, "."), (4, "▁"), (4, ","), (5, "▁and"), (6, "▁has")]);

        rig.FeedChunks(1);
        var result = rig.Recognizer.FlushUtterance();

        Assert.Equal("the there., and has", result.Final);
        Assert.Equal(["the", "there.,", "and", "has"], result.FinalWords!.Select(w => w.Word));
        Assert.Equal(WhitespaceWords(result.Final!), result.FinalWords!.Count);
    }

    [Fact]
    public void AMarkInFrontOfTheFirstWord_ShouldStayItsOwnWordInTextAndWords()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "."), (2, "▁the"), (3, "▁there")]);

        rig.FeedChunks(1);
        var result = rig.Recognizer.FlushUtterance();

        Assert.Equal(". the there", result.Final);
        Assert.Equal([".", "the", "there"], result.FinalWords!.Select(w => w.Word));
    }

    [Fact]
    public void LoneTagWithoutLetters_ShouldNotCloseAnythingAndLeadTheNextUtterance()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "<ru-RU>"), (2, "▁the"), (3, "."), (3, "▁"), (3, "<en-US>")]);

        var result = rig.Feed(new float[N]);

        Assert.Equal("<ru-RU> the. <en-US>", result.Final);
        Assert.Equal(["the."], result.FinalWords!.Select(w => w.Word));
    }

    // ── endpointing ─────────────────────────────────────────────────────

    [Fact]
    public void Silence_OfStopHistoryEouSeconds_ShouldCloseTheUtteranceInTheChunkWhoseEndMakesTheGap()
    {
        // last letter in frame 0; the gap is chunkEndFrame - 0 - 1 >= 13 frames (1.04 s): chunk 1 ends at frame 14
        using var rig = Make(RawOptions(), tokens: [(0, "▁the")]);

        var results = rig.FeedChunks(3);

        Assert.Null(results[0].Final);
        Assert.Equal("the", results[0].Partial);
        Assert.Equal("the", results[1].Final);
        Assert.Equal("", results[1].Partial);
        Assert.Null(results[2].Final);
    }

    [Fact]
    public void Silence_OneFrameShort_ShouldNotCloseTheUtterance()
    {
        // the gap after the last letter in frame 1 is 14 - 1 - 1 = 12 frames at the end of chunk 1, 19 at the end of chunk 2
        using var rig = Make(RawOptions(), tokens: [(1, "▁the")]);

        var results = rig.FeedChunks(3);

        Assert.Null(results[0].Final);
        Assert.Null(results[1].Final);
        Assert.Equal("the", results[2].Final);
    }

    [Fact]
    public void Silence_Measured_ShouldNotCountPunctuationOrTagsAsSpeech()
    {
        // a late "." in frame 12 must not restart the silence clock: the gap is measured from the last LETTER (frame 1)
        using var rig = Make(RawOptions(), tokens: [(1, "▁the"), (12, ".")]);

        var results = rig.FeedChunks(3);

        Assert.Null(results[1].Final);
        Assert.Equal("the.", results[2].Final);
    }

    [Fact]
    public void Silence_BetweenTwoTokensInOneCall_ShouldSplitInFrontOfTheSecond()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "▁the"), (40, "▁there")]);

        var result = rig.Feed(new float[6 * N]);            // one call, six chunks

        Assert.Equal("the", result.Final);                   // closed at the end of chunk 2, before the second word was heard
        Assert.Equal("there", result.Partial);
        Assert.Equal(["the"], result.FinalWords!.Select(w => w.Word));
        Assert.Equal(["there"], result.PartialWords!.Select(w => w.Word));
    }

    [Fact]
    public void DroppedChunks_ShouldCountAsSilenceForTheGap()
    {
        // a 10 s limit (125 frames); the gate drops silent chunks from the sixth on, they still advance the stream position
        using var rig = Make(RawOptions(eou: 10), useVad: true, speech: index => index == 0, tokens: [(3, "▁the")]);
        var audio = TestAudio.Ramp(25 * N);

        var results = new List<StreamingResult>();
        for (var k = 0; k < 25; k++)
            results.Add(rig.Feed(audio[(k * N)..((k + 1) * N)]));

        // letter in frame 3: the gap reaches 125 frames at the end of chunk 18 (frame 133)
        var final = results.FindIndex(r => r.Final is not null);
        Assert.Equal(18, final);
        Assert.Equal(6, rig.Engine.Calls.Count);             // chunks 0 to 5 were encoded, the rest dropped
        Assert.Equal("the", results[final].Final);
    }

    [Fact]
    public void MaxUtteranceSeconds_ShouldSplitInFrontOfAWordButNeverInsideOne()
    {
        // words every 2 frames; a 1.2 s cap = 15 frames; "▁prin" in frame 14 and "ci" in frame 15 form one word that must stay whole
        (int, string)[] tokens =
        [
            (0, "▁the"), (2, "▁and"), (4, "▁has"), (6, "▁the"), (8, "▁and"), (10, "▁has"), (12, "▁the"),
            (14, "▁prin"), (15, "ci"), (16, "p"), (18, "▁and"), (20, "▁the"),
        ];
        using var rig = Make(RawOptions(cap: 1.2), tokens: tokens);

        rig.FeedChunks(3);
        rig.Results.Add(rig.Recognizer.FlushUtterance());
        var finals = rig.Results.Where(r => r.Final is not null).Select(r => r.Final!).ToList();

        // the cap is reached at frame 15 (first letter in frame 0); the next word start is "▁and" in frame 18, so the pieces of "princip" stay together
        Assert.Equal(["the and has the and has the princip", "and the"], finals);
    }

    [Fact]
    public void MaxUtteranceSeconds_Zero_ShouldNotCap()
    {
        var tokens = Enumerable.Range(0, 60).Select(i => (i * 2, "▁the")).ToArray();
        using var rig = Make(RawOptions(cap: 0), tokens: tokens);

        rig.FeedChunks(18);

        Assert.DoesNotContain(rig.Results, r => r.Final is not null);
        Assert.Equal(60, rig.Results[^1].PartialWords!.Count);
    }

    [Fact]
    public void ClosersThatArriveAfterASilenceEnd_ShouldLeadTheNextUtterance()
    {
        // "there" closed by silence at chunk 2; its "." and tag come with the next speech in chunk 3
        using var rig = Make(RawOptions(), tokens: [(1, "▁there"), (22, "."), (22, "▁"), (22, "<en-US>"), (23, "▁the"), (24, "▁and")]);

        var results = rig.FeedChunks(4);

        Assert.Equal("there", results[2].Final);
        Assert.Null(results[3].Final);
        Assert.Equal(". the and", results[3].Partial);
        Assert.Equal([".", "the", "and"], results[3].PartialWords!.Select(w => w.Word));
    }

    [Fact]
    public void ALeadingMark_ShouldSitOnTheStartOfTheFirstWordAndNotMoveTheUtteranceEarlier()
    {
        // the mark was emitted at frame 22 (1.76 s), the first word at frame 25 (2.0 s)
        using var rig = Make(RawOptions(), tokens: [(22, "."), (25, "▁the"), (26, "▁and"), (30, "."), (30, "<en-US>")]);

        var results = rig.FeedChunks(5);
        var result = results.Single(r => r.Final is not null);

        Assert.Equal([".", "the", "and."], result.FinalWords!.Select(w => w.Word));
        Assert.Equal(2.0, result.FinalWords![0].StartSeconds, 6);
        Assert.Equal(2.0, result.FinalWords[0].EndSeconds, 6);     // a mark has no speech of its own
        Assert.Equal(2.0, result.FinalWords[1].StartSeconds, 6);
        Assert.All(result.FinalWords[0].Tokens!, t => Assert.Equal(2.0, t.StartSeconds, 6));
        Assert.Equal(WhitespaceWords(result.Final!), result.FinalWords.Count);
    }

    [Fact]
    public void AMarkWithoutAnyWord_ShouldKeepItsOwnTime()
    {
        using var rig = Make(RawOptions(), tokens: [(22, ".")]);

        var results = rig.FeedChunks(4);

        var mark = Assert.Single(results[3].PartialWords!);
        Assert.Equal(".", results[3].Partial);
        Assert.Equal(1.76, mark.StartSeconds, 6);
    }

    [Fact]
    public void Flush_ShouldCommitAMarkThatNoWordFollowedAsAContinuingFinal()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "▁there"), (22, "."), (22, "▁"), (22, "<en-US>")]);
        rig.FeedChunks(3);                                   // "there" is closed at chunk 2
        rig.Results.Add(rig.Feed(new float[N / 2]));         // half a chunk is buffered

        var tail = rig.Recognizer.FlushUtterance();          // the padded chunk 3 holds the late closers

        Assert.Equal("there", rig.Results.Single(r => r.Final is not null).Final);
        Assert.Equal(". <en-US>", tail.Final);               // nothing is left open after a flush
        Assert.True(tail.FinalContinuesPrevious);
        Assert.Equal(".", Assert.Single(tail.FinalWords!).Word);
        Assert.Equal("", tail.Partial);
        Assert.False(tail.PartialContinuesPrevious);
        Assert.Equal("there. <en-US>", UtteranceText.Join("there", tail.Final, tail.FinalContinuesPrevious));
    }

    [Fact]
    public void Flush_ShouldAttachAClosingMarkToTheFinalOfTheSameCall()
    {
        // "there" at frame 1, its "." 13 frames later in the padded chunk of the flush: the silence rule closes "there" on that very
        // token and the mark leads a new utterance, which the flush must not leave open
        using var rig = Make(RawOptions(keepTag: false), tokens: [(1, "▁there"), (15, ".")]);
        rig.Feed(new float[2 * N + N / 2]);                  // chunks 0 and 1, half a chunk buffered

        var tail = rig.Recognizer.FlushUtterance();

        Assert.Equal("there.", tail.Final);
        Assert.False(tail.FinalContinuesPrevious);
        var word = Assert.Single(tail.FinalWords!);
        Assert.Equal("there.", word.Word);
        Assert.Equal(1 * Frame, word.StartSeconds, 6);
        Assert.Equal(2 * Frame, word.EndSeconds, 6);         // the mark does not extend the word
        Assert.Equal("", tail.Partial);
        Assert.Empty(tail.PartialWords!);
    }

    [Fact]
    public void Flush_ShouldKeepTheClosingTagOfAMarkItCommits()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "▁there"), (15, "."), (15, "▁"), (15, "<en-US>")]);
        rig.Feed(new float[2 * N + N / 2]);

        var tail = rig.Recognizer.FlushUtterance();

        Assert.Equal("there. <en-US>", tail.Final);
        Assert.Equal("there.", Assert.Single(tail.FinalWords!).Word);
        Assert.Equal("en-US", rig.Recognizer.LastLanguageTag);
    }

    [Fact]
    public void Flush_WithOnlyALanguageTagOpen_ShouldCommitNothing()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "▁there"), (15, "<en-US>")]);

        var results = rig.FeedChunks(3);
        var tail = rig.Recognizer.FlushUtterance();

        Assert.Equal("there", results[2].Final);             // the silence rule closes it on the tag, which leads the next utterance
        Assert.Null(tail.Final);                             // a tag alone is no text: nothing to commit
        Assert.Equal("", tail.Partial);
        Assert.Equal("en-US", rig.Recognizer.LastLanguageTag);
    }

    [Fact]
    public void Flush_ShouldFinalizeTheTrailingWords()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "▁the"), (2, "▁there")]);
        rig.FeedChunks(1);

        var tail = rig.Recognizer.FlushUtterance();

        Assert.Equal("the there", tail.Final);
        Assert.Equal("", tail.Partial);
        Assert.Equal(2, tail.FinalWords!.Count);
    }

    // ── the clock ───────────────────────────────────────────────────────

    [Fact]
    public void WordTimes_ShouldBeOnTheStreamClockIncludingDroppedChunks()
    {
        // speech in chunk 0, then a long silence the gate drops, then a word in chunk 25 (frame 175 + 2)
        using var rig = Make(RawOptions(eou: 100), useVad: true, speech: index => index == 0 || index >= 25, tokens: [(2, "▁the"), (177, "▁there")]);
        var audio = TestAudio.Ramp(26 * N);

        for (var k = 0; k < 26; k++)
            rig.Feed(audio[(k * N)..((k + 1) * N)]);
        var tail = rig.Recognizer.FlushUtterance();

        Assert.True(rig.Recognizer.Engine.ChunksDropped > 0);
        var words = rig.Results.Concat([tail]).SelectMany(r => r.FinalWords ?? []).ToList();
        Assert.Equal(["the", "there"], words.Select(w => w.Word));
        Assert.Equal(177 * Frame, words[1].StartSeconds, 6);  // the dropped chunks kept their place
    }

    [Fact]
    public void WordTimes_AfterAMidStreamFlush_ShouldNotCountThePaddingAsAudio()
    {
        // 1.5 chunks fed and flushed: chunk 1 is padded with 4480 zeros that were never fed; the next chunk starts that much earlier on the clock
        using var rig = Make(RawOptions(), tokens: [(8, "▁the"), (16, "▁there")]);
        rig.Feed(new float[N + N / 2]);
        var flushed = rig.Recognizer.FlushUtterance();
        rig.Feed(new float[N]);
        var tail = rig.Recognizer.FlushUtterance();

        Assert.Equal(8 * 1280 / 16000.0, flushed.FinalWords![0].StartSeconds, 6);
        // frame 16 lies at grid sample 20480; 4480 padded samples before it: 16000 samples = 1.0 s
        Assert.Equal(1.0, tail.FinalWords![0].StartSeconds, 6);
    }

    [Fact]
    public void WordTimes_AfterAFlushWithClosingChunks_ShouldNotCountThemAsAudio()
    {
        // 1.5 chunks fed and flushed with one closing chunk: chunk 1 is padded with 4480 zeros, chunk 2 is all zeros; the late word in the
        // closing chunk is clamped to the end of the audio, the word of the next stream part starts where its audio starts
        var engine = new FakeChunkEngine(N, F) { Script = Script(F, (15, "▁go"), (21, "▁there")) };
        var plain = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value,
            () => new FakeGate(N, _ => true), options: new NemotronRecognizerOptions { FlushClosingChunks = 1 });
        using var recognizer = new NemotronUtteranceRecognizer(plain, RawOptions());

        recognizer.ProcessUtterance(new float[N + N / 2]);
        var flushed = recognizer.FlushUtterance();
        recognizer.ProcessUtterance(new float[N]);
        var tail = recognizer.FlushUtterance();

        var fed = (N + N / 2) / 16000.0;
        Assert.Equal("go", Assert.Single(flushed.FinalWords!).Word);
        Assert.Equal(fed, flushed.FinalWords![0].StartSeconds, 6);
        // frame 21 lies at grid sample 26880; 4480 + 8960 padded samples before it: 13440 samples = 0.84 s, where the second part begins
        Assert.Equal(fed, tail.FinalWords![0].StartSeconds, 6);
    }

    [Fact]
    public void WordTimes_ShouldBeClampedToTheAudioFed()
    {
        // a token in the last frame of the padded chunk is emitted beyond the 0.84 s that were fed
        using var rig = Make(RawOptions(), tokens: [(13, "▁the")]);
        rig.Feed(new float[N + N / 2 - 1]);
        rig.Feed(new float[1]);

        var tail = rig.Recognizer.FlushUtterance();

        var word = Assert.Single(tail.FinalWords!);
        var fed = (N + N / 2) / 16000.0;
        Assert.Equal(fed, word.StartSeconds, 6);
        Assert.Equal(fed, word.EndSeconds, 6);
    }

    [Fact]
    public void WordTimes_ShouldNeverBeNegativeOrUnordered()
    {
        using var rig = Make(CalibratedOptions(), tokens: [(0, "▁the"), (1, "▁there"), (2, "▁and"), (3, "▁has")]);

        var result = rig.Feed(new float[N]);
        var words = result.PartialWords!;

        Assert.Equal(4, words.Count);
        Assert.All(words, w => Assert.True(w.StartSeconds >= 0 && w.EndSeconds >= w.StartSeconds));
        for (var i = 1; i < words.Count; i++)
        {
            Assert.True(words[i].StartSeconds >= words[i - 1].StartSeconds);
            Assert.True(words[i].StartSeconds >= words[i - 1].EndSeconds - 1e-9);
        }
    }

    // ── calibration ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("0", 0.224, 0.144)]       // English
    [InlineData("11", 0.298, 0.178)]      // Russian
    [InlineData("9", 0.150, 0.104)]       // German: not measured, the late-biased default
    public void WordStart_ShouldSubtractTheCalibratedLagOfTheConfiguredLanguage(string langId, double startLag, double endLag)
    {
        using var rig = Make(CalibratedOptions(), langId: langId, tokens: [(20, "▁the")]);

        var word = Assert.Single(rig.Feed(new float[3 * N]).PartialWords!);

        Assert.Equal(20 * Frame - startLag + 0.015, word.StartSeconds, 3);       // late bias 15 ms
        Assert.Equal(21 * Frame - endLag, word.EndSeconds, 3);
    }

    [Fact]
    public void WordStart_OfAnUtteranceWithATag_ShouldUseTheLagOfTheTaggedLanguage()
    {
        // configured English, but the model says Russian: the tag wins once the utterance ends
        using var rig = Make(CalibratedOptions(), langId: "0", tokens: [(20, "▁the"), (21, "."), (21, "▁"), (21, "<ru-RU>")]);

        var result = rig.Feed(new float[4 * N]);

        Assert.Equal(20 * Frame - 0.298 + 0.015, result.FinalWords![0].StartSeconds, 3);
    }

    [Fact]
    public void WordStart_WithOverrides_ShouldUseThem()
    {
        var timing = NemotronWordTimingOptions.Default with
        {
            Snap = null,
            LateBiasSeconds = 0,
            LanguageOverrides = new Dictionary<string, NemotronLagCalibration> { ["en"] = new(0.100, 0.050) },
        };
        using var rig = Make(new NemotronUtteranceOptions { WordTiming = timing }, tokens: [(20, "▁the")]);

        var word = Assert.Single(rig.Feed(new float[3 * N]).PartialWords!);

        Assert.Equal(20 * Frame - 0.100, word.StartSeconds, 6);
        Assert.Equal(21 * Frame - 0.050, word.EndSeconds, 6);
    }

    [Fact]
    public void LagTable_ShouldHaveEveryMeasuredEntryAndALateBiasedDefault()
    {
        var options = NemotronWordTimingOptions.Default;
        foreach (var chunk in new[] { NemotronLagTable.Chunk560, NemotronLagTable.Chunk1120 })
        {
            var en = NemotronLagTable.Lookup(chunk, "en", options);
            var ru = NemotronLagTable.Lookup(chunk, "ru", options);
            var unknown = NemotronLagTable.Lookup(chunk, "de", options);
            Assert.True(ru.StartLagSeconds > en.StartLagSeconds);                      // Russian is emitted later
            Assert.True(unknown.StartLagSeconds < en.StartLagSeconds);                 // unknown: start later (safer)
            Assert.Equal(unknown, NemotronLagTable.Lookup(chunk, null, options));
        }
        Assert.True(NemotronLagTable.Lookup(NemotronLagTable.Chunk1120, "en", options).StartLagSeconds
                    < NemotronLagTable.Lookup(NemotronLagTable.Chunk560, "en", options).StartLagSeconds);
    }

    // ── batching independence, reset, plain interface ───────────────────

    private static (int, string)[] Dialogue() =>
    [
        (1, "▁the"), (2, "▁there"), (3, ","), (4, "▁and"), (5, "▁has"), (6, "."), (6, "▁"), (6, "<en-US>"),
        (8, "▁the"), (9, "▁H"), (9, "ar"), (10, "'"), (10, "s"), (12, "?"), (12, "▁"), (12, "<en-US>"),
        (30, "▁be"), (31, "en"), (31, "."), (50, "▁and"), (51, "▁has"),
    ];

    [Theory]
    [InlineData(1280)]       // the clean front-end looks 80 ms ahead of every chunk
    [InlineData(3200)]
    public void EngineWithLookahead_ShouldGiveTheSameWordsAsOneWithout(int lookahead)
    {
        // an engine with a look-ahead holds a chunk back until the samples behind it are there: the Finals come in later calls (and
        // the last chunks at the flush), but the stream positions of the tokens, so the words, are the same
        var audio = TestAudio.Ramp(9 * N + 1234);
        List<WordTiming> Words(FakeChunkEngine engine)
        {
            engine.Script = Script(F, Dialogue());
            var inner = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value, () => new FakeGate(N, _ => true));
            using var recognizer = new NemotronUtteranceRecognizer(inner, CalibratedOptions());
            var results = TestAudio.Split(audio, 3000).Select(piece => recognizer.ProcessUtterance(piece)).ToList();
            results.Add(recognizer.FlushUtterance());
            return results.SelectMany(r => r.FinalWords ?? []).ToList();
        }

        var without = Words(new FakeChunkEngine(N, F));
        var with = Words(new FakeChunkEngine(N, F, lookahead));

        Assert.NotEmpty(without);
        Assert.Equal(without.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)), with.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)));
    }

    [Fact]
    public void Words_OfABurstAfterAPause_ShouldKeepTheirOrderWithAndWithoutAGapBetweenTheChunks()
    {
        // the model shows a pause's worth of words in one go (three consecutive frames after 3 s of silence): the words keep their
        // order and spacing however the audio is batched
        (int, string)[] burst = [(40, "▁the"), (41, "▁there"), (42, "▁and")];
        var audio = new float[8 * N];
        List<WordTiming> Run(params int[] sizes)
        {
            using var rig = Make(CalibratedOptions(), tokens: burst);
            var results = TestAudio.Split(audio, sizes).Select(p => rig.Recognizer.ProcessUtterance(p)).ToList();
            results.Add(rig.Recognizer.FlushUtterance());
            return results.SelectMany(r => r.FinalWords ?? []).ToList();
        }

        var aligned = Run(N);
        var ragged = Run(1, 7, 4999, 333);

        Assert.Equal(["the", "there", "and"], aligned.Select(w => w.Word));
        Assert.Equal(aligned.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)), ragged.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)));
        AssertNoOverlap(aligned);
        Assert.True(aligned[1].StartSeconds - aligned[0].StartSeconds >= Frame - 1e-9 && aligned[2].StartSeconds - aligned[1].StartSeconds >= Frame - 1e-9);
    }

    [Fact]
    public void RaggedBatches_ShouldGiveTheSameFinalsPartialsAndWordTimes()
    {
        var audio = TestAudio.Ramp(9 * N + 1234);
        List<StreamingResult> Run(params int[] sizes)
        {
            using var rig = Make(CalibratedOptions(), tokens: Dialogue());
            var results = new List<StreamingResult>();
            foreach (var piece in TestAudio.Split(audio, sizes))
                results.Add(rig.Recognizer.ProcessUtterance(piece));
            results.Add(rig.Recognizer.FlushUtterance());
            return results;
        }

        var aligned = Run(N);
        // A call reports every utterance that ended in it (joined by a space), so compare the flat stream of texts and words.
        string Digest(List<StreamingResult> results) =>
            string.Join(' ', results.Where(r => r.Final is not null).Select(r => r.Final))
            + "#" + string.Join(",", results.SelectMany(r => r.FinalWords ?? []).Select(w => $"{w.Word}@{w.StartSeconds:F6}-{w.EndSeconds:F6}"))
            + "||" + results[^1].Partial;

        var expected = Digest(aligned);
        Assert.Contains("the there, and has. <en-US> the Har's? <en-US> been.", expected);
        foreach (var sizes in new[] { new[] { 800 }, [3200], [1], [7, 4999, 20001, 333], [100000], [N + 1, 5, 3 * N] })
            Assert.Equal(expected, Digest(Run(sizes)));
    }

    [Fact]
    public void ResetStreamingState_ShouldStartANewStreamAtTimeZero()
    {
        using var rig = Make(RawOptions(), tokens: [(2, "▁the"), (3, "▁there")]);
        rig.Feed(new float[N]);
        rig.Recognizer.FlushUtterance();

        rig.Recognizer.ResetStreamingState();
        var again = rig.Feed(new float[N]);

        Assert.Equal(1, rig.Engine.ResetCount);
        Assert.Equal("the there", again.Partial);
        Assert.Equal(2 * Frame, again.PartialWords![0].StartSeconds, 6);
        Assert.Null(rig.Recognizer.LastLanguageTag);
    }

    [Fact]
    public void ResetStreamingState_ShouldDropTheOpenUtteranceAndPendingMarks()
    {
        using var rig = Make(RawOptions(), tokens: [(2, "▁the"), (3, "▁"), (3, "<ru-RU>")]);
        rig.Feed(new float[N / 2]);                      // buffered, not encoded
        rig.Recognizer.ResetStreamingState();

        var result = rig.Feed(new float[N]);

        Assert.Equal("the <ru-RU>", result.Final);
        Assert.Equal(0, rig.Engine.Calls[0].ChunkIndex);
    }

    [Fact]
    public void ProcessAudio_Plain_ShouldReturnTheEngineTextWithTagsAndKeepTheBookkeeping()
    {
        using var rig = Make(RawOptions(), tokens: [(2, "▁the"), (3, "▁there"), (5, "."), (5, "▁"), (5, "<en-US>"), (9, "▁and")]);

        var first = rig.Recognizer.ProcessAudio(new float[N]);
        Assert.Equal(" the there. <en-US>", first);
        Assert.Equal(5, rig.Recognizer.LastTokenCount);
        Assert.Null(rig.Recognizer.PartialText);                                   // never provisional text on the plain interface
        Assert.Equal("", rig.Recognizer.ProcessUtterance([]).Partial);             // the tag closed the utterance

        var second = rig.Recognizer.ProcessAudio(new float[N]);
        Assert.Equal(" and", second);
        Assert.Equal("and", rig.Recognizer.ProcessUtterance([]).Partial);
        Assert.Null(rig.Recognizer.ProcessAudio(new float[10]));   // less than a chunk
        Assert.Equal("", rig.Recognizer.Flush());                  // the padded chunk has no tokens
        Assert.Equal("", rig.Recognizer.ProcessUtterance([]).Partial);             // the flush closed the open utterance
    }

    [Fact]
    public void TranscribeWords_ShouldEqualTheStreamingWords()
    {
        var audio = new float[3 * N + 100];
        using var rig = Make(CalibratedOptions(), tokens: Dialogue());

        var streamed = new List<WordTiming>();
        foreach (var piece in TestAudio.Split(audio, N))
            if (rig.Recognizer.ProcessUtterance(piece).FinalWords is { } w)
                streamed.AddRange(w);
        if (rig.Recognizer.FlushUtterance().FinalWords is { } tail)
            streamed.AddRange(tail);

        var offline = rig.Recognizer.TranscribeWords(audio);

        Assert.NotEmpty(streamed);
        Assert.Equal(streamed.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)), offline.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)));
    }

    [Fact]
    public void TranscribeWords_OfAnIdleRecognizer_ShouldNotLoadASecondEngineAndLeaveItReset()
    {
        var created = 0;
        var audio = new float[3 * N + 100];
        using var rig = Make(CalibratedOptions(), createOfflineEngine: () => { created++; return NewEngine(N, F, null, false, null, Dialogue()).Recognizer; }, tokens: Dialogue());

        var first = rig.Recognizer.TranscribeWords(audio);
        var second = rig.Recognizer.TranscribeWords(audio);

        Assert.Equal(0, created);
        Assert.NotEmpty(first);
        Assert.Equal(first.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)), second.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)));
        Assert.Equal(0, rig.Recognizer.Engine.ChunksConsumed);       // idle again
        Assert.Equal("", rig.Recognizer.ProcessUtterance([]).Partial);
    }

    [Fact]
    public void TranscribeWords_DuringAStream_ShouldUseASecondEngineAndLeaveTheStreamUntouched()
    {
        var created = new List<FakeChunkEngine>();
        var audio = new float[3 * N + 100];
        var offline = (Func<NemotronRnntRecognizer>)(() =>
        {
            var (recognizer, engine) = NewEngine(N, F, null, false, null, Dialogue());
            created.Add(engine);
            return recognizer;
        });
        using var interrupted = Make(CalibratedOptions(), createOfflineEngine: offline, tokens: Dialogue());
        using var straight = Make(CalibratedOptions(), tokens: Dialogue());

        var pieces = TestAudio.Split(audio, N).ToList();
        var expected = pieces.Select(p => straight.Recognizer.ProcessUtterance(p)).ToList();
        var actual = new List<StreamingResult>();
        IReadOnlyList<WordTiming>? words = null;
        for (var k = 0; k < pieces.Count; k++)
        {
            actual.Add(interrupted.Recognizer.ProcessUtterance(pieces[k]));
            if (k == 1)
                words = interrupted.Recognizer.TranscribeWords(audio);   // in the middle of the stream
        }
        actual.Add(interrupted.Recognizer.FlushUtterance());
        expected.Add(straight.Recognizer.FlushUtterance());

        Assert.Single(created);                                          // one extra engine, reused afterwards
        Assert.Equal(expected.Select(r => (r.Final, r.Partial)), actual.Select(r => (r.Final, r.Partial)));
        Assert.Equal(expected.SelectMany(r => r.FinalWords ?? []).Select(w => (w.Word, w.StartSeconds, w.EndSeconds)),
            actual.SelectMany(r => r.FinalWords ?? []).Select(w => (w.Word, w.StartSeconds, w.EndSeconds)));
        Assert.Equal(expected.SelectMany(r => r.FinalWords ?? []).Select(w => (w.Word, w.StartSeconds, w.EndSeconds)),
            words!.Select(w => (w.Word, w.StartSeconds, w.EndSeconds)));
        Assert.Equal(straight.Engine.Calls.Count, interrupted.Engine.Calls.Count);   // the main engine saw only its own audio

        interrupted.Recognizer.TranscribeWords(audio);
        Assert.Single(created);
    }

    [Fact]
    public void TranscribeWords_ShouldReturnTheWordsOfTheTranscriptWhenAMarkAndALatePieceLeadTheNextFinal()
    {
        // "there" is closed by the silence rule; its "." and the tag come with the next speech, and the rest of "been" 30 frames
        // after its first piece: the Finals say 'there' | '. the and' | 'be' | 'en'
        (int, string)[] tokens = [(1, "▁there"), (22, "."), (22, "▁"), (22, "<en-US>"), (23, "▁the"), (24, "▁and"), (50, "▁be"), (80, "en")];
        var audio = new float[12 * N];
        using var streaming = Make(CalibratedOptions(), tokens: tokens);
        using var offline = Make(CalibratedOptions(), tokens: tokens);

        var results = TestAudio.Split(audio, N).Select(p => streaming.Recognizer.ProcessUtterance(p)).ToList();
        results.Add(streaming.Recognizer.FlushUtterance());
        var transcript = "";
        foreach (var result in results.Where(r => r.Final is not null))
            transcript = UtteranceText.Join(transcript, result.Final, result.FinalContinuesPrevious);

        var words = offline.Recognizer.TranscribeWords(audio);

        var streamed = results.SelectMany(r => r.FinalWords ?? []).ToList();
        Assert.Equal("there. <en-US> the and been", System.Text.RegularExpressions.Regex.Replace(transcript, @"\s+", " ").Trim());
        Assert.Equal(["there.", "the", "and", "been"], words.Select(w => w.Word));                    // one word per word of the text, no entry that is only a mark
        Assert.Equal(streamed.First(w => w.Word == "the").StartSeconds, words[1].StartSeconds);
        Assert.Equal(streamed.First(w => w.Word == "be").StartSeconds, words[3].StartSeconds);
        Assert.Equal(streamed.First(w => w.Word == "en").EndSeconds, words[3].EndSeconds);            // the late piece extends the word
        Assert.Equal(streamed.First(w => w.Word == "there").EndSeconds, words[0].EndSeconds);          // the mark does not
    }

    [Fact]
    public void TranscribeWords_OfAnIdleRecognizer_ShouldStartFromAFreshGainAndLeaveTheOneItLearned()
    {
        var loud = TestAudio.Syllables(2 * N, 0.1f);
        var quiet = TestAudio.Syllables(4 * N, 0.005f, seed: 2);
        using var learned = Make(CalibratedOptions() with { AutoGain = true }, tokens: Dialogue());
        using var newOne = Make(CalibratedOptions() with { AutoGain = true }, tokens: Dialogue());
        using var control = Make(CalibratedOptions() with { AutoGain = true }, tokens: Dialogue());
        learned.FeedChunks(2, loud);
        learned.Recognizer.ResetStreamingState();                                    // idle again, but the gain remembers the loud microphone
        control.FeedChunks(2, loud);
        control.Recognizer.ResetStreamingState();
        learned.Engine.Calls.Clear();
        control.Engine.Calls.Clear();

        learned.Recognizer.TranscribeWords(quiet);
        newOne.Recognizer.TranscribeWords(quiet);

        // the pass heard what a new recognizer hears, not the level the loud stream left behind
        Assert.Equal(newOne.Engine.Calls.Select(c => c.Chunk), learned.Engine.Calls.Select(c => c.Chunk));
        // and the stream goes on with the level it had, as if nothing had been transcribed
        learned.Engine.Calls.Clear();
        learned.Recognizer.ResetStreamingState();
        learned.FeedChunks(4, quiet);
        control.FeedChunks(4, quiet);
        Assert.Equal(control.Engine.Calls.Select(c => c.Chunk), learned.Engine.Calls.Select(c => c.Chunk));
    }

    [Fact]
    public void TranscribeWords_DuringAStream_ShouldHearWhatANewRecognizerHearsAndLeaveTheStreamsGain()
    {
        var loud = TestAudio.Syllables(2 * N, 0.1f);
        var quiet = TestAudio.Syllables(4 * N, 0.005f, seed: 2);
        var created = new List<FakeChunkEngine>();
        var offline = (Func<NemotronRnntRecognizer>)(() =>
        {
            var (recognizer, engine) = NewEngine(N, F, null, false, null, Dialogue());
            created.Add(engine);
            return recognizer;
        });
        using var streaming = Make(CalibratedOptions() with { AutoGain = true }, createOfflineEngine: offline, tokens: Dialogue());
        using var newOne = Make(CalibratedOptions() with { AutoGain = true }, tokens: Dialogue());
        using var control = Make(CalibratedOptions() with { AutoGain = true }, tokens: Dialogue());
        streaming.FeedChunks(2, loud);
        control.FeedChunks(2, loud);

        streaming.Recognizer.TranscribeWords(quiet);                                  // a stream is in progress: the second engine
        streaming.Recognizer.TranscribeWords(quiet);                                  // and the same again: it does not remember the first pass
        newOne.Recognizer.TranscribeWords(quiet);

        var firstPass = created.Single().Calls.Take(4).Select(c => c.Chunk).ToList();
        var secondPass = created.Single().Calls.Skip(4).Take(4).Select(c => c.Chunk).ToList();
        Assert.Equal(newOne.Engine.Calls.Take(4).Select(c => c.Chunk), firstPass);
        Assert.Equal(firstPass, secondPass);
        streaming.Engine.Calls.Clear();
        control.Engine.Calls.Clear();
        streaming.FeedChunks(2, quiet);
        control.FeedChunks(2, quiet);
        Assert.Equal(control.Engine.Calls.Select(c => c.Chunk), streaming.Engine.Calls.Select(c => c.Chunk));
    }

    [Fact]
    public void Settings_ShouldBeForwardedToTheEngine()
    {
        using var rig = Make(RawOptions(), tokens: []);

        Assert.True(rig.Recognizer.TrySetLanguage("ru"));
        Assert.Equal(11, rig.Recognizer.Engine.LanguageId);
        Assert.True(rig.Recognizer.TrySetSearchOptions(1, 1.0));
        Assert.Equal(1.04, rig.Recognizer.StopHistoryEouSeconds);
        Assert.Equal(N, rig.Recognizer.ChunkSamples);
        Assert.Equal(16000, rig.Recognizer.SampleRate);
    }

    [Fact]
    public void Dispose_ShouldDisposeTheEngineAndRejectFurtherUse()
    {
        var rig = Make(RawOptions(), tokens: []);
        rig.Recognizer.Dispose();

        Assert.True(rig.Engine.Disposed);
        Assert.Throws<ObjectDisposedException>(() => rig.Recognizer.ProcessUtterance(new float[N]));
        Assert.Throws<ObjectDisposedException>(() => rig.Recognizer.FlushUtterance());
    }

    [Fact]
    public void Constructor_WithANonPositiveEouSeconds_ShouldThrow()
    {
        var engine = new FakeChunkEngine();
        var inner = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value, () => new FakeGate(N, _ => true));

        Assert.Throws<ArgumentOutOfRangeException>(() => new NemotronUtteranceRecognizer(inner, new NemotronUtteranceOptions { StopHistoryEouSeconds = 0 }));
    }

    // ── the 1120 ms variant ─────────────────────────────────────────────

    [Fact]
    public void ChunkSize1120_ShouldUseItsOwnFramesAndLag()
    {
        using var rig = Make(CalibratedOptions(), chunk: 17920, frames: 14, langId: "0", tokens: [(30, "▁the")]);

        var word = Assert.Single(rig.Feed(new float[3 * 17920]).PartialWords!);

        Assert.Equal(30 * Frame - 0.182 + 0.015, word.StartSeconds, 3);
        // the silence gap works on frames, not on chunks: letter in frame 30, chunk 2 ends at frame 42: 11 < 13; chunk 3 at 56: 25 >= 13
        using var rig2 = Make(RawOptions(), chunk: 17920, frames: 14, tokens: [(30, "▁the")]);
        var results = rig2.FeedChunks(4);
        Assert.Null(results[2].Final);
        Assert.Equal("the", results[3].Final);
    }

    // ── gain control ────────────────────────────────────────────────────

    private const float QuietSyllableRms = 0.005f;   // about -46 dBFS: the gain raises it about 6 times, to the target

    private static float Rms(float[] samples) => (float)Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);

    [Fact]
    public void AutoGain_ShouldRaiseQuietInputBeforeTheEngineAndKeepTheStreamClock()
    {
        var quiet = TestAudio.Syllables(4 * N, QuietSyllableRms);
        using var raw = Make(CalibratedOptions(), tokens: [(2, "▁the")]);
        using var gained = Make(CalibratedOptions() with { AutoGain = true }, tokens: [(2, "▁the")]);

        var rawResults = raw.FeedChunks(4, quiet);
        var gainedResults = gained.FeedChunks(4, quiet);

        Assert.Equal(quiet[(3 * N)..], raw.Engine.Calls[3].Chunk);                       // off: the model hears the input as it is
        var boost = Rms(gained.Engine.Calls[3].Chunk) / Rms(raw.Engine.Calls[3].Chunk);
        Assert.InRange(boost, AutomaticGainControl.TargetRms / QuietSyllableRms * 0.8f, AutomaticGainControl.TargetRms / QuietSyllableRms * 1.2f);
        Assert.Equal(raw.Engine.Calls.Select(c => c.Chunk.Length), gained.Engine.Calls.Select(c => c.Chunk.Length));
        Assert.Equal(rawResults[0].PartialWords![0].StartSeconds, gainedResults[0].PartialWords![0].StartSeconds);   // same clock
    }

    [Fact]
    public void AutoGain_OfNormallyLeveledInput_ShouldReachTheEngineUnchanged()
    {
        var loud = TestAudio.Syllables(2 * N, 0.1f);
        using var rig = Make(RawOptions() with { AutoGain = true }, tokens: []);

        rig.FeedChunks(2, loud);

        Assert.Equal(loud[..N], rig.Engine.Calls[0].Chunk);
        Assert.Equal(loud[N..], rig.Engine.Calls[1].Chunk);
    }

    [Fact]
    public void AutoGain_ShouldNotDependOnHowTheAudioIsBatched()
    {
        var quiet = TestAudio.Syllables(5 * N, 0.004f);
        using var aligned = Make(RawOptions() with { AutoGain = true }, tokens: []);
        using var ragged = Make(RawOptions() with { AutoGain = true }, tokens: []);

        aligned.FeedChunks(5, quiet);
        foreach (var piece in TestAudio.Split(quiet, 1, 7, 4999, 20001, 333, 800))
            ragged.Feed(piece);

        Assert.Equal(aligned.Engine.Calls.Select(c => c.Chunk), ragged.Engine.Calls.Select(c => c.Chunk));
    }

    [Fact]
    public void AutoGain_ShouldKeepItsLevelAcrossAReset()
    {
        // loud speech, then a quiet stretch: the level decays slowly, so the first quiet chunk is not yet boosted fully.
        // A reset in between (a new recording session on the same microphone) must not change that trajectory.
        var loudThenQuiet = TestAudio.Syllables(N, 0.1f).Concat(TestAudio.Syllables(2 * N, QuietSyllableRms, seed: 2)).ToArray();
        using var continuous = Make(RawOptions() with { AutoGain = true }, tokens: []);
        using var withReset = Make(RawOptions() with { AutoGain = true }, tokens: []);

        continuous.FeedChunks(3, loudThenQuiet);
        withReset.Feed(loudThenQuiet[..N]);
        withReset.Recognizer.ResetStreamingState();
        withReset.Engine.Calls.Clear();
        withReset.Feed(loudThenQuiet[N..]);

        Assert.Equal(continuous.Engine.Calls.Skip(1).Select(c => c.Chunk), withReset.Engine.Calls.Select(c => c.Chunk));
        // and it is not simply a boost from the first sample: a gain that started afresh would differ
        using var fresh = Make(RawOptions() with { AutoGain = true }, tokens: []);
        fresh.Feed(loudThenQuiet[N..]);
        Assert.NotEqual(withReset.Engine.Calls[0].Chunk, fresh.Engine.Calls[0].Chunk);
    }

    [Fact]
    public void AutoGain_ShouldAlsoApplyToTheWordsOfTranscribeWords()
    {
        var quiet = TestAudio.Syllables(4 * N, QuietSyllableRms);
        using var rig = Make(CalibratedOptions() with { AutoGain = true }, tokens: Dialogue());

        rig.Recognizer.TranscribeWords(quiet);

        var boost = Rms(rig.Engine.Calls[3].Chunk) / Rms(quiet[(3 * N)..]);
        Assert.InRange(boost, AutomaticGainControl.TargetRms / QuietSyllableRms * 0.8f, AutomaticGainControl.TargetRms / QuietSyllableRms * 1.2f);
    }

    // ── words of consecutive utterances ─────────────────────────────────

    private static void AssertNoOverlap(IReadOnlyList<WordTiming> words)
    {
        for (var i = 1; i < words.Count; i++)
        {
            Assert.True(words[i].StartSeconds >= words[i - 1].EndSeconds - 1e-9,
                $"'{words[i].Word}' starts at {words[i].StartSeconds:F3}, before '{words[i - 1].Word}' ends at {words[i - 1].EndSeconds:F3}");
            Assert.True(words[i].StartSeconds >= words[i - 1].StartSeconds - 1e-9);
        }
    }

    // fast speech: a word in every frame, split by the length cap of 0.64 s (8 frames) in front of the word at frame 28
    private static NemotronUtteranceOptions CappedCalibratedOptions() =>
        new() { MaxUtteranceSeconds = 8 * Frame, WordTiming = NemotronWordTimingOptions.Default with { Snap = null } };

    private static readonly (int, string)[] WordEveryFrame = Enumerable.Range(20, 11).Select(f => (f, f % 2 == 0 ? "▁the" : "▁there")).ToArray();

    [Fact]
    public void Words_OfAnUtteranceThatStartsRightBehindAFinal_ShouldNotOverlapTheLastWordOfThatFinal()
    {
        // the calibrated start of the word at frame 28 (2.031 s) lies 0.065 s before the calibrated end of the word at frame 27
        // (2.096 s): words of different utterances are clamped like the words inside one
        using var rig = Make(CappedCalibratedOptions(), tokens: WordEveryFrame);

        var results = rig.FeedChunks(5);
        var final = results.Single(r => r.Final is not null);
        var open = results[^1].PartialWords!;
        var tail = rig.Recognizer.FlushUtterance();

        Assert.Equal(8, final.FinalWords!.Count);
        Assert.Equal(3, open.Count);
        AssertNoOverlap(final.FinalWords!.Concat(open).ToList());                   // the partial words of the open utterance
        AssertNoOverlap(final.FinalWords!.Concat(tail.FinalWords!).ToList());       // and the words it ends up with
        Assert.Equal(final.FinalWords![^1].EndSeconds, tail.FinalWords![0].StartSeconds, 6);    // late is harmless: the start moved, the word stays
    }

    [Fact]
    public void Words_OfUtterancesFinishedInOneCall_ShouldNotOverlap()
    {
        // the cap ends the first utterance and the silence rule the second, both inside one call (TakeResult joins the two lists)
        using var rig = Make(CappedCalibratedOptions(), tokens: WordEveryFrame);

        var result = rig.Feed(new float[8 * N]);

        Assert.Equal(11, result.FinalWords!.Count);
        AssertNoOverlap(result.FinalWords!);
    }

    // ── an open utterance that continues the previous one ───────────────

    [Fact]
    public void PartialContinuesPrevious_ShouldBeSetForAMarkOrAWordPieceAtTheStartOfTheOpenUtterance()
    {
        // "there" is committed by the silence rule in chunk 2; what follows in chunk 3 leads the next utterance
        (int, string)[] withMark = [(1, "▁there"), (22, "."), (22, "▁"), (22, "<en-US>"), (23, "▁the")];
        (int, string)[] withPiece = [(1, "▁there"), (22, "s"), (23, "▁and")];
        (int, string)[] withWord = [(1, "▁there"), (23, "▁and")];
        using var mark = Make(RawOptions(), tokens: withMark);
        using var piece = Make(RawOptions(), tokens: withPiece);
        using var word = Make(RawOptions(), tokens: withWord);

        var markResults = mark.FeedChunks(4);
        var pieceResults = piece.FeedChunks(4);
        var wordResults = word.FeedChunks(4);

        Assert.Equal("there", markResults[2].Final);
        Assert.False(markResults[2].PartialContinuesPrevious);                             // nothing open yet
        Assert.Equal((". the", true), (markResults[3].Partial, markResults[3].PartialContinuesPrevious));
        Assert.Equal(". the", markResults[3].Stable);
        Assert.Equal(("s and", true), (pieceResults[3].Partial, pieceResults[3].PartialContinuesPrevious));
        Assert.Equal(("and", false), (wordResults[3].Partial, wordResults[3].PartialContinuesPrevious));

        // the live text a consumer composes: no space in front of the mark or the piece
        Assert.Equal("there. the", UtteranceText.Join(markResults[2].Final, markResults[3].Partial, markResults[3].PartialContinuesPrevious));
        Assert.Equal("theres and", UtteranceText.Join(pieceResults[2].Final, pieceResults[3].Partial, pieceResults[3].PartialContinuesPrevious));
        Assert.Equal("there and", UtteranceText.Join(wordResults[2].Final, wordResults[3].Partial, wordResults[3].PartialContinuesPrevious));
    }

    [Fact]
    public void PartialContinuesPrevious_ShouldBeFalseForTheFirstTokensOfAFreshUtteranceAndAfterAFlush()
    {
        using var rig = Make(RawOptions(), tokens: [(1, "▁the"), (2, "▁there")]);

        var open = rig.Feed(new float[N]);
        var flushed = rig.Recognizer.FlushUtterance();

        Assert.False(open.PartialContinuesPrevious);
        Assert.Equal("the there", flushed.Final);
        Assert.False(flushed.FinalContinuesPrevious);
        Assert.False(flushed.PartialContinuesPrevious);
    }

    // ── what gets typed into another application ────────────────────────

    [Fact]
    public void TypedText_OfTheWinUiComposition_ShouldGrowWithoutRepeatsAndWithoutASpaceBeforeAMark()
    {
        // "the there and" is committed by the silence rule; the model writes its "." and the tag only when "has been" begins
        (int, string)[] tokens =
        [
            (1, "▁the"), (2, "▁there"), (3, "▁and"), (24, "."), (24, "▁"), (24, "<en-US>"), (25, "▁has"), (26, "▁be"), (27, "en"),
            (34, "."), (34, "▁"), (34, "<en-US>"),
        ];
        using var rig = Make(RawOptions(keepTag: false), tokens: tokens);
        var accumulated = new System.Text.StringBuilder();
        var typer = new SpeechLib.TextOutput.IncrementalTextTyper();
        var stripTags = new SpeechLib.PostProcessing.PostProcessingChain().Add(new SpeechLib.PostProcessing.LanguageTagStripper());
        var typed = new System.Text.StringBuilder();
        var snapshots = new List<string>();

        // the composition of VoiceType.WinUI RecognitionService.HandleStreamingResult: finalized utterances plus the stable open one
        void Handle(StreamingResult result)
        {
            if (!string.IsNullOrEmpty(result.Final))
                UtteranceText.Append(accumulated, result.Final, result.FinalContinuesPrevious);
            var committed = UtteranceText.Join(accumulated.ToString(), result.Stable, result.PartialContinuesPrevious);
            if (committed.Length == 0)
                return;
            var delta = typer.Next(stripTags.Execute(committed) ?? "");
            typed.Append(delta);
            snapshots.Add(typed.ToString());
        }
        foreach (var result in rig.FeedChunks(7))
            Handle(result);
        Handle(rig.Recognizer.FlushUtterance());

        Assert.Equal("the there and. has been.", System.Text.RegularExpressions.Regex.Replace(typed.ToString(), @"\s+", " ").Trim());
        Assert.DoesNotContain(" .", typed.ToString());                                       // the mark joins the sentence it closes
        for (var i = 1; i < snapshots.Count; i++)
            Assert.StartsWith(snapshots[i - 1], snapshots[i], StringComparison.Ordinal);      // typed text is never taken back
    }

    // ── warm-up ─────────────────────────────────────────────────────────

    [Fact]
    public void Warmup_ShouldLeaveTheStreamClockAtZero()
    {
        using var warmed = Make(RawOptions(), tokens: []);
        using var fresh = Make(RawOptions(), tokens: []);
        using var unreset = Make(RawOptions(), tokens: []);
        foreach (var rig in new[] { warmed, fresh, unreset })
            rig.Engine.Script = (_, _) => [(Id("▁the"), 5)];             // one word in frame 5 of every chunk

        RecognizerWarmup.Run(warmed.Recognizer);
        unreset.Recognizer.ProcessAudio(new float[N]);                    // the old order: a silent chunk, no reset after it

        var first = warmed.Feed(new float[N]).PartialWords![0];
        Assert.Equal(5 * Frame, first.StartSeconds, 6);
        Assert.Equal(fresh.Feed(new float[N]).PartialWords![0].StartSeconds, first.StartSeconds);
        Assert.Equal(0, warmed.Engine.Calls[1].ChunkIndex);                // the chunk after the warm-up is chunk 0 again
        Assert.Equal(first.StartSeconds + N / 16000.0, unreset.Feed(new float[N]).PartialWords![^1].StartSeconds, 6);   // one chunk late
    }
}
