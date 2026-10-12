using SpeechLib;
using SpeechLib.Models;
using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class DecoderWordTimingsTests
{
    private const int Rate = 1000; // 1 sample = 1 ms

    private static WordTiming W(string word, double start, double end) =>
        new() { Word = word, StartSeconds = start, EndSeconds = end };

    [Fact]
    public void Map_ShouldCarryDecoderTimesOntoTheTextWordsWithAnOffset()
    {
        var words = DecoderWordTimings.Map([W("Hello", 1.0, 1.4), W("world.", 1.5, 2.0)], ["Hello", "world."], offsetSeconds: 10);

        Assert.NotNull(words);
        Assert.Equal([(11.0, 11.4), (11.5, 12.0)], words.Select(w => (w.Start, w.End)));
        Assert.Equal(["Hello", "world."], words.Select(w => w.Text));
    }

    [Fact]
    public void Map_ShouldReturnNullWhenTheWordsDoNotLineUp()
    {
        Assert.Null(DecoderWordTimings.Map([W("a", 0, 1)], ["a", "b"]));
        Assert.Null(DecoderWordTimings.Map(null, ["a"]));
        Assert.Null(DecoderWordTimings.Map([], ["a"]));
    }

    [Fact]
    public void Map_ShouldMergeADetachedMarkInTheTextIntoThePreviousWord()
    {
        // The recognizer text has "alright ." (two words), the decoder grouped the mark into "alright.".
        var words = DecoderWordTimings.Map([W("alright.", 1, 2)], ["alright", "."]);

        var word = Assert.Single(words!);
        Assert.Equal("alright.", word.Text);
    }

    [Fact]
    public void Map_ShouldSkipLeadingMarksThatLeftTheText()
    {
        var words = DecoderWordTimings.Map([W(".", 0, 0.1), W("Next", 0.5, 0.9)], ["Next"]);

        var word = Assert.Single(words!);
        Assert.Equal((0.5, 0.9), (word.Start, word.End));
    }

    // ── OfflineTranscriber with a timed recognizer ──────────────────────────

    private sealed class TimedScript(Func<long, StreamingResult> step, StreamingResult flush) : ITimedUtteranceRecognizer
    {
        public long Fed { get; private set; }
        public int SampleRate => Rate;
        public int ChunkSamples => 100;
        public double StopHistoryEouSeconds => 0.5;
        public StreamingResult ProcessUtterance(float[] chunk) { Fed += chunk.Length; return step(Fed); }
        public StreamingResult FlushUtterance() => flush;
        public IReadOnlyList<WordTiming> TranscribeWords(float[] waveform) => [];
        public string? ProcessAudio(float[] chunk) => throw new NotSupportedException();
        public string? Flush() => throw new NotSupportedException();
        public void ResetStreamingState() => Fed = 0;
        public void Dispose() { }
    }

    private static float[] Audio(double seconds) => new float[(int)(seconds * Rate)];

    [Fact]
    public void Run_ShouldUseDecoderWordTimesAndSpanTheSegmentFromFirstToLastWord()
    {
        var recognizer = new TimedScript(
            fed => fed == 3500
                ? new StreamingResult("", "Hello world.") { FinalWords = [W("Hello", 1.2, 1.6), W("world.", 1.7, 2.9)] }
                : new StreamingResult(fed > 1000 && fed < 3500 ? "Hello" : "", null),
            new StreamingResult("", null));

        var result = OfflineTranscriber.Run(recognizer, Audio(6), new OfflineTranscriptionOptions());

        var segment = Assert.Single(result.Document.Segments);
        Assert.Equal(WordTimingKinds.Decoder, segment.WordTiming);
        Assert.Equal((1.2, 2.9), (segment.Start, segment.End));
        Assert.Equal([(1.2, 1.6), (1.7, 2.9)], segment.Words.Select(w => (w.Start, w.End)));
        Assert.Equal(["Hello", "world."], segment.Words.Select(w => w.Text));
    }

    [Fact]
    public void Run_ShouldFallBackToEstimatedWhenTheDecoderWordsDoNotMatchTheText()
    {
        var recognizer = new TimedScript(
            fed => fed == 3500
                ? new StreamingResult("", "Hello big world.") { FinalWords = [W("Hello", 1.2, 1.6), W("world.", 1.7, 2.9)] }
                : new StreamingResult(fed > 1000 && fed < 3500 ? "Hello" : "", null),
            new StreamingResult("", null));

        var result = OfflineTranscriber.Run(recognizer, Audio(6), new OfflineTranscriptionOptions());

        Assert.Equal(WordTimingKinds.Estimated, Assert.Single(result.Document.Segments).WordTiming);
    }

    [Fact]
    public void Run_ShouldEstimateWhenDecoderTimingsAreSwitchedOff()
    {
        var recognizer = new TimedScript(
            fed => fed == 3500
                ? new StreamingResult("", "Hello world.") { FinalWords = [W("Hello", 1.2, 1.6), W("world.", 1.7, 2.9)] }
                : new StreamingResult(fed > 1000 && fed < 3500 ? "Hello" : "", null),
            new StreamingResult("", null));

        var result = OfflineTranscriber.Run(recognizer, Audio(6), new OfflineTranscriptionOptions { UseDecoderTimings = false });

        Assert.Equal(WordTimingKinds.Estimated, Assert.Single(result.Document.Segments).WordTiming);
    }

    [Fact]
    public void Run_ShouldPutResumedStreamTimesBackOnTheAudioTimeline()
    {
        // A resumed run feeds the audio from 2.0 s: the recognizer's stream starts there.
        var recognizer = new TimedScript(
            fed => fed == 1500
                ? new StreamingResult("", "Late words") { FinalWords = [W("Late", 0.3, 0.6), W("words", 0.7, 1.1)] }
                : new StreamingResult("", null),
            new StreamingResult("", null));

        var result = OfflineTranscriber.Run(recognizer, Audio(6), new OfflineTranscriptionOptions { StartSample = 2000 });

        var segment = Assert.Single(result.Document.Segments);
        Assert.Equal([(2.3, 2.6), (2.7, 3.1)], segment.Words.Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void Run_ShouldMoveTheLeadingMarkOfTheNextCommitToThePreviousSegment()
    {
        var recognizer = new TimedScript(
            fed => fed switch
            {
                2000 => new StreamingResult("", "First one") { FinalWords = [W("First", 0.2, 0.5), W("one", 0.6, 0.9)] },
                4000 => new StreamingResult("", ". Second") { FinalWords = [W(".", 2.5, 2.6), W("Second", 3.0, 3.4)] },
                _ => new StreamingResult("", null),
            },
            new StreamingResult("", null));

        var result = OfflineTranscriber.Run(recognizer, Audio(6), new OfflineTranscriptionOptions());

        Assert.Equal(2, result.Document.Segments.Count);
        Assert.Equal("First one.", result.Document.Segments[0].Text);
        Assert.Equal("one.", result.Document.Segments[0].Words[^1].Text);
        Assert.Equal((3.0, 3.4), (result.Document.Segments[1].Start, result.Document.Segments[1].End));
        Assert.All(result.Document.Segments, s => Assert.Equal(WordTimingKinds.Decoder, s.WordTiming));
    }

    // ── LiveSegmentTimer ────────────────────────────────────────────────────

    [Fact]
    public void LiveTimer_ShouldUseDecoderWordTimesOfACommit()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.5);
        timer.OnBatch(1000, "Hello", null);
        timer.OnBatch(1000, "Hello world.", null);
        timer.OnBatch(1000, "", "Hello world.", committedWords: [W("Hello", 0.8, 1.2), W("world.", 1.4, 1.9)]);

        var segment = Assert.Single(timer.Segments);
        Assert.Equal(WordTimingKinds.Decoder, segment.WordTiming);
        Assert.Equal((0.8, 1.9), (segment.Start, segment.End));
        Assert.Equal([(0.8, 1.2), (1.4, 1.9)], segment.Words.Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void LiveTimer_ShouldKeepEstimatingWithoutDecoderWords()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.5);
        timer.OnBatch(1000, "Hello", null);
        timer.OnBatch(1000, "", "Hello world.");

        Assert.Equal(WordTimingKinds.Estimated, Assert.Single(timer.Segments).WordTiming);
    }

    [Fact]
    public void LiveTimer_ShouldUseDecoderWordTimesOfTheFlush()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.5);
        timer.OnBatch(2000, "Tail words", null);
        timer.OnFlush("Tail words", [W("Tail", 0.5, 0.8), W("words", 0.9, 1.5)]);

        var segment = Assert.Single(timer.Segments);
        Assert.Equal(WordTimingKinds.Decoder, segment.WordTiming);
        Assert.Equal((0.5, 1.5), (segment.Start, segment.End));
    }
}
