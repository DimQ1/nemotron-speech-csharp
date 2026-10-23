using SpeechLib;
using SpeechLib.Models;
using SpeechLib.PostProcessing;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

/// <summary>
/// How the live loop turns recognizer output into the transcript and the text on screen: the join of utterances (a closing mark or
/// the rest of a word that leads the next utterance gets no space), the composition with the open utterance, the tail after a flush.
/// </summary>
public class RecognitionPipelinesTests
{
    private static readonly PostProcessingChain PartialPostProcessing =
        new PostProcessingChain().Add(new LanguageTagStripper());

    private static readonly PostProcessingChain FinalPostProcessing =
        new PostProcessingChain().Add(new LanguageTagStripper()).Add(new WhitespaceNormalizer());

    private static WordTiming W(string word, double start, double end) =>
        new() { Word = word, StartSeconds = start, EndSeconds = end };

    /// <summary>An utterance recognizer that answers each batch with the next scripted result.</summary>
    private sealed class ScriptedUtterances(IEnumerable<StreamingResult> steps, StreamingResult flush) : IUtteranceStreamingRecognizer
    {
        private readonly Queue<StreamingResult> _steps = new(steps);
        public int SampleRate => 16000;
        public int ChunkSamples => 8960;
        public double StopHistoryEouSeconds => 1.04;
        public StreamingResult ProcessUtterance(float[] chunk) => _steps.Dequeue();
        public StreamingResult FlushUtterance() => flush;
        public string? ProcessAudio(float[] chunk) => throw new NotSupportedException();
        public string? Flush() => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class ScriptedPlain(IEnumerable<string?> deltas, string? partial = null, string? flush = null) : IStreamingSpeechRecognizer
    {
        private readonly Queue<string?> _deltas = new(deltas);
        public int SampleRate => 16000;
        public int ChunkSamples => 8960;
        public string? PartialText => partial;
        public string? ProcessAudio(float[] chunk) => _deltas.Dequeue();
        public string? Flush() => flush;
        public void Dispose() { }
    }

    private static readonly float[] Batch = new float[800];

    private static StreamingResult Open(string partial, bool continues = false) =>
        new(partial, null) { Stable = partial, PartialContinuesPrevious = continues };

    [Fact]
    public void Create_ShouldChooseThePipelineByTheRecognizerInterface()
    {
        Assert.IsType<UtterancePipeline>(RecognitionPipelineFactory.Create(new ScriptedUtterances([], new StreamingResult("", null))));
        Assert.IsType<PlainPipeline>(RecognitionPipelineFactory.Create(new ScriptedPlain([])));
    }

    [Fact]
    public void Utterance_ShouldJoinTheClosingMarkThatLeadsTheNextFinalWithoutASpace()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [
                Open("They went home"),
                new StreamingResult("", "They went home"),                                       // committed by the silence rule
                Open(". You", continues: true),                                                     // the mark leads the open utterance
                new StreamingResult("", ". <en-US> You are so rude <en-US>") { FinalContinuesPrevious = true },
            ],
            new StreamingResult("", null)));

        var steps = Enumerable.Range(0, 4).Select(_ => pipeline.Process(Batch)).ToList();

        Assert.Equal("They went home", steps[0].Display);
        Assert.Equal("They went home", steps[1].Display);
        Assert.Equal("They went home. You", steps[2].Display);                                  // live text: no space in front of the mark
        Assert.Equal("They went home. <en-US> You are so rude <en-US>", pipeline.CommittedText);
        Assert.Equal("They went home. You are so rude", FinalPostProcessing.Execute(pipeline.CommittedText));
        Assert.True(steps[3].CommittedContinuesPrevious);
        Assert.False(steps[1].CommittedContinuesPrevious);
    }

    // ── a recognizer that behaves like Parakeet TDT: utterances end in silence, nothing continues the previous one, the open utterance
    //    has a stable part followed by a revisable preview, and the words carry the decoder's times ──

    [Fact]
    public void Utterance_OfAParakeetStyleRecognizer_ShouldJoinSentencesWithASpaceAndShowStablePartBeforeThePreview()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [
                new StreamingResult("How are", null) { Stable = "How" },                                            // preview after the stable part
                new StreamingResult("", "How are you doing") { FinalWords = [W("How", 1, 1.2), W("are", 1.2, 1.4), W("you", 1.4, 1.6), W("doing", 1.6, 2)] },
                new StreamingResult("Fine thanks", null) { Stable = "Fine" },
                new StreamingResult("", "Fine thanks"),
            ],
            new StreamingResult("", null)));

        var steps = Enumerable.Range(0, 4).Select(_ => pipeline.Process(Batch)).ToList();

        Assert.Equal("How are", steps[0].Display);
        Assert.Equal("How are you doing", steps[1].Display);
        Assert.Equal("How are you doing Fine thanks", steps[2].Display);                        // a plain space between sentences, as before
        Assert.False(steps[1].CommittedContinuesPrevious);
        Assert.Equal("How are you doing Fine thanks", pipeline.CommittedText);
        Assert.Equal(["How", "are", "you", "doing"], steps[1].CommittedWords!.Select(w => w.Word));
    }

    [Fact]
    public void Utterance_OfAParakeetStyleRecognizer_ShouldJoinAFinalThatStartsWithAMarkWithoutASpace()
    {
        // not only Nemotron: any recognizer whose Final starts with a sentence mark is joined without the space that the plain
        // concatenation used to put in front of it ("Is it ready ." before, "Is it ready." now)
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [new StreamingResult("", "Is it ready"), new StreamingResult("", "? Yes it is")], new StreamingResult("", null)));

        pipeline.Process(Batch);
        var second = pipeline.Process(Batch);

        Assert.Equal("Is it ready? Yes it is", pipeline.CommittedText);
        Assert.Equal("Is it ready? Yes it is", second.Display);
    }

    [Fact]
    public void Utterance_ShouldJoinTheRestOfALateWordWithoutASpace()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [
                new StreamingResult("", "The mouse is reliable"),
                Open("s are good", continues: true),
                new StreamingResult("", "s are good <en-US>") { FinalContinuesPrevious = true },
            ],
            new StreamingResult("", null)));

        var steps = Enumerable.Range(0, 3).Select(_ => pipeline.Process(Batch)).ToList();

        Assert.Equal("The mouse is reliables are good", steps[1].Display);
        Assert.Equal("The mouse is reliables are good <en-US>", pipeline.CommittedText);
    }

    [Fact]
    public void Utterance_ShouldSeparateOrdinaryUtterancesWithASpaceAndComposeTheOpenOne()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [
                new StreamingResult("Second", "First one."),
                new StreamingResult("", "Second one"),
            ],
            new StreamingResult("", null)));

        var first = pipeline.Process(Batch);
        var second = pipeline.Process(Batch);

        Assert.Equal("First one. Second", first.Display);                                       // committed text, then the open utterance
        Assert.Equal("Second", first.Tail);
        Assert.Equal("First one.", first.Committed);
        Assert.True(first.IsUtteranceCommit);
        Assert.Equal("First one. Second one", second.Display);
        Assert.Equal("First one. Second one", pipeline.CommittedText);
    }

    [Fact]
    public void Utterance_ShouldHandOnTheWordsOfACommitOnly()
    {
        IReadOnlyList<WordTiming> words = [W("Hello", 1, 1.4), W("world", 1.5, 2)];
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [
                new StreamingResult("", null) { FinalWords = words },                           // not a commit: its words are not reported
                new StreamingResult("", "Hello world") { FinalWords = words },
            ],
            new StreamingResult("", null)));

        var none = pipeline.Process(Batch);
        var commit = pipeline.Process(Batch);

        Assert.Null(none.CommittedWords);
        Assert.Null(none.Committed);
        Assert.False(none.IsUtteranceCommit);
        Assert.Same(words, commit.CommittedWords);
    }

    [Fact]
    public void Utterance_Flush_ShouldCommitAMarkThatNoWordFollowed()
    {
        // "tonight" was committed by the silence rule; the model's "." arrived afterwards and the stream ended
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [new StreamingResult("", "The game is tonight")],
            new StreamingResult("", ".") { FinalContinuesPrevious = true, FinalWords = [W(".", 9, 9)] }));      // a flush leaves nothing open
        pipeline.Process(Batch);

        var tail = pipeline.Flush();

        Assert.Equal(".", tail.Committed);
        Assert.True(tail.IsUtteranceCommit);
        Assert.True(tail.CommittedContinuesPrevious);
        Assert.Equal("The game is tonight.", pipeline.CommittedText);
        Assert.Equal("The game is tonight.", tail.Display);
    }

    [Fact]
    public void Utterance_Flush_ShouldCommitAClosingMarkThatCameWithTheLastWords()
    {
        // the model's "." came 13 frames after "tonight", inside the padded chunk of the flush: the Final of that call has both
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [new StreamingResult("", "The game is")],
            new StreamingResult("", "tonight.") { FinalWords = [W("tonight.", 8, 8.4)] }));
        pipeline.Process(Batch);

        var tail = pipeline.Flush();

        Assert.Equal("tonight.", tail.Committed);
        Assert.Equal("The game is tonight.", pipeline.CommittedText);
        Assert.Equal("The game is tonight.", tail.Display);
    }

    [Fact]
    public void Utterance_Flush_ShouldCommitTheLastUtterance()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances([Open("Last words")], new StreamingResult("", "Last words")));
        pipeline.Process(Batch);

        var tail = pipeline.Flush();

        Assert.Equal("Last words", tail.Committed);
        Assert.Equal("Last words", pipeline.CommittedText);
    }

    [Fact]
    public void Utterance_Reset_ShouldDropTheCommittedText()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances([new StreamingResult("", "One"), new StreamingResult("", "Two")], new StreamingResult("", null)));
        pipeline.Process(Batch);

        pipeline.Reset();
        var step = pipeline.Process(Batch);

        Assert.Equal("Two", pipeline.CommittedText);
        Assert.Equal("Two", step.Display);
    }

    [Fact]
    public void Utterance_StableText_ShouldAddTheStablePartOfTheOpenUtteranceToTheCommittedText()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [
                new StreamingResult("Second one", "First one.") { Stable = "Second" },           // a revisable preview after the stable part
                new StreamingResult("Second one", null),                                         // a recognizer that reports no stable part
                Open(". Third", continues: true),                                               // the closing mark of the previous utterance
            ],
            new StreamingResult("", null)));

        var first = pipeline.Process(Batch);
        var firstStable = pipeline.StableText;
        pipeline.Process(Batch);
        var noStable = pipeline.StableText;
        pipeline.Reset();
        var afterReset = pipeline.StableText;
        pipeline.Process(Batch);

        Assert.Equal("First one. Second one", first.Display);
        Assert.Equal("First one. Second", firstStable);
        Assert.Equal("First one.", noStable);
        Assert.Equal("", afterReset);
        Assert.Equal(". Third", pipeline.StableText);                                           // nothing committed yet: no joining space
    }

    [Fact]
    public void Utterance_StableText_ShouldJoinAMarkThatLeadsTheOpenUtteranceWithoutASpace()
    {
        var pipeline = new UtterancePipeline(new ScriptedUtterances(
            [new StreamingResult("", "They went home"), Open(". You", continues: true)],
            new StreamingResult("", null)));

        pipeline.Process(Batch);
        pipeline.Process(Batch);

        Assert.Equal("They went home. You", pipeline.StableText);
    }

    [Fact]
    public void Plain_StableText_ShouldBeTheCommittedText()
    {
        var pipeline = new PlainPipeline(new ScriptedPlain([" Hello", " world."], partial: "maybe"));

        pipeline.Process(Batch);
        pipeline.Process(Batch);

        Assert.Equal(" Hello world.", pipeline.StableText);
    }

    /// <summary>
    /// What <c>MainViewModel</c> types while recording: <c>RecognitionService.RaiseCommitted</c> hands the stable text (language tags
    /// stripped) to <see cref="LiveTextFeed"/> after every step and after the flush, <c>FinalResult</c> (tags stripped, blanks
    /// normalized) after Stop. Returns everything that reached the focused window.
    /// </summary>
    private static string TypedWhileRecording(IEnumerable<StreamingResult> steps, StreamingResult flush)
    {
        var script = steps.ToList();
        var pipeline = new UtterancePipeline(new ScriptedUtterances(script, flush));
        var feed = new LiveTextFeed();
        var typed = new System.Text.StringBuilder();
        string? last = null;

        void Raise()
        {
            var text = PartialPostProcessing.Execute(pipeline.StableText);
            if (text.Length == 0 || text == last)
                return;
            last = text;
            typed.Append(feed.Next(text, canType: true));
        }

        foreach (var _ in script)
        {
            pipeline.Process(Batch);
            Raise();
        }

        pipeline.Flush();
        Raise();
        typed.Append(feed.Next(FinalPostProcessing.Execute(pipeline.CommittedText), canType: true));
        return typed.ToString();
    }

    [Fact]
    public void Utterance_ShouldTypeTheWholeTextWhenFinalsCarryLanguageTags()
    {
        // The Uno app keeps the closing language tag in the Final (History reads the language from it). The tag sits in the middle of
        // a Final that continues the previous utterance; once the tag is stripped the committed text must still extend what was typed
        // from the tag-free open utterance, or the typer sees a rewrite and drops the closing "." of "rude".
        var typed = TypedWhileRecording(
            [
                Open("They went home"),
                new StreamingResult("", "They went home"),
                Open(". You", continues: true),
                Open(". You are so rude", continues: true),
                new StreamingResult("", ". <en-US> You are so rude. <en-US>") { FinalContinuesPrevious = true },
                Open("Next"),
            ],
            new StreamingResult("", "Next one. <en-US>"));

        Assert.Equal("They went home. You are so rude. Next one.", typed);
    }

    [Fact]
    public void Utterance_ShouldTypeTheClosingMarkOfTheLastSentenceThatTheFlushCommits()
    {
        var typed = TypedWhileRecording(
            [Open("The game is tonight"), new StreamingResult("", "The game is tonight")],
            new StreamingResult("", ".") { FinalContinuesPrevious = true });

        Assert.Equal("The game is tonight.", typed);
    }

    [Fact]
    public void Plain_ShouldCommitEveryDeltaVerbatimAndComposeTheRevisableTail()
    {
        var pipeline = new PlainPipeline(new ScriptedPlain([" Hello", null, " world."], partial: "maybe", flush: " Bye."));

        var first = pipeline.Process(Batch);
        var second = pipeline.Process(Batch);
        var third = pipeline.Process(Batch);
        var tail = pipeline.Flush();

        Assert.Equal((" Hello maybe", " Hello", false), (first.Display, first.Committed, first.IsUtteranceCommit));
        Assert.Null(second.Committed);
        Assert.Equal(" world.", third.Committed);
        Assert.Equal(" Bye.", tail.Committed);
        Assert.Equal(" Hello world. Bye.", pipeline.CommittedText);
        Assert.False(tail.IsUtteranceCommit);
        Assert.Null(first.CommittedWords);
    }
}
