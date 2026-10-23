using SpeechLib;
using SpeechLib.Models;
using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class OfflineTranscriberTests : IDisposable
{
    private const int Rate = 1000; // 1 sample = 1 ms keeps the arithmetic readable
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    /// <summary>Utterance recognizer driven by a script over the number of samples fed since reset.</summary>
    private sealed class ScriptedUtteranceRecognizer(Func<long, StreamingResult> step, StreamingResult flush, double eou = 0.5)
        : IUtteranceStreamingRecognizer
    {
        public long Fed { get; private set; }
        public int SampleRate => Rate;
        public int ChunkSamples => 100;
        public double StopHistoryEouSeconds => eou;
        public StreamingResult ProcessUtterance(float[] chunk) { Fed += chunk.Length; return step(Fed); }
        public StreamingResult FlushUtterance() => flush;
        public string? ProcessAudio(float[] chunk) => throw new NotSupportedException();
        public string? Flush() => throw new NotSupportedException();
        public void ResetStreamingState() => Fed = 0;
        public void Dispose() { }
    }

    private sealed class ScriptedPlainRecognizer(Func<long, string?> step, string? flush) : IStreamingSpeechRecognizer
    {
        public int SampleRate => Rate;
        public int ChunkSamples => 100;
        private long _fed;
        public string? ProcessAudio(float[] chunk) { _fed += chunk.Length; return step(_fed); }
        public string? Flush() => flush;
        public void ResetStreamingState() => _fed = 0;
        public void Dispose() { }
    }

    private static float[] Audio(double seconds) => new float[(int)(seconds * Rate)];

    [Fact]
    public void Run_ShouldTimeUtterancesFromPartialStartToCommitMinusEou()
    {
        // Speech 1.0–3.0 s, committed at 3.5 s (0.5 s EOU); second utterance from 5.0 s, flushed.
        var recognizer = new ScriptedUtteranceRecognizer(
            fed => fed switch
            {
                > 1000 and < 3500 => new StreamingResult("Hello", null),
                3500 => new StreamingResult("", "Hello world."),
                > 5000 => new StreamingResult("Second", null),
                _ => new StreamingResult("", null),
            },
            new StreamingResult("", "Second one"));
        var checkpoints = new List<OfflineCheckpoint>();
        var progress = new List<OfflineProgress>();

        var result = OfflineTranscriber.Run(recognizer, Audio(10), new OfflineTranscriptionOptions
        {
            Checkpoint = checkpoints.Add,
            Progress = progress.Add,
        });

        var segments = result.Document.Segments;
        Assert.Equal(2, segments.Count);
        Assert.Equal((1.0, 3.0, "Hello world."), (segments[0].Start, segments[0].End, segments[0].Text));
        Assert.Equal((5.0, 10.0, "Second one"), (segments[1].Start, segments[1].End, segments[1].Text));
        Assert.Equal([0, 1], segments.Select(s => s.Index));
        Assert.Equal("Hello world. Second one", result.Document.Text);

        // Estimated word timings cover the segment span.
        Assert.Equal(["Hello", "world."], segments[0].Words.Select(w => w.Text));
        Assert.Equal(1.0, segments[0].Words[0].Start);
        Assert.Equal(3.0, segments[0].Words[^1].End);
        Assert.Equal(WordTimingKinds.Estimated, segments[0].WordTiming);

        var checkpoint = Assert.Single(checkpoints);
        Assert.Equal(3500, checkpoint.Sample);
        Assert.Single(checkpoint.Segments);
        Assert.Equal(10000, progress[^1].ProcessedSamples);
        Assert.Equal(1.0, progress[^1].Fraction);
    }

    [Fact]
    public void Run_ShouldResumeFromCheckpointWithCommittedSegments()
    {
        var committed = new List<TranscriptSegment>
        {
            new() { Index = 0, Start = 1, End = 3, Text = "Hello world." },
        };
        var recognizer = new ScriptedUtteranceRecognizer(
            fed => fed > 1000 ? new StreamingResult("Tail", null) : new StreamingResult("", null),
            new StreamingResult("", "Tail end"));

        var result = OfflineTranscriber.Run(recognizer, Audio(10), new OfflineTranscriptionOptions
        {
            StartSample = 3500,
            CommittedSegments = committed,
        });

        Assert.Equal(6500, recognizer.Fed); // only the rest of the audio is decoded
        Assert.Equal(["Hello world.", "Tail end"], result.Document.Segments.Select(s => s.Text));
        Assert.Equal(4.5, result.Document.Segments[1].Start); // absolute time, not relative to the resume point
        Assert.Equal(1, result.Document.Segments[1].Index);
    }

    private static WordTiming W(string word, double start, double end) =>
        new() { Word = word, StartSeconds = start, EndSeconds = end };

    // ── what a transducer reports with the NEXT utterance: closing marks, the rest of a late word, tag-closed Finals ──

    [Fact]
    public void Run_ShouldMoveALeadingMarkAndTheRestOfALateWordToThePreviousSegment()
    {
        var recognizer = new ScriptedUtteranceRecognizer(
            fed => fed switch
            {
                3500 => new StreamingResult("", "Reading able") { FinalWords = [W("Reading", 1.0, 1.4), W("able", 1.5, 2.0)] },
                8500 => new StreamingResult("", "s are good") { FinalContinuesPrevious = true, FinalWords = [W("s", 6.0, 6.1), W("are", 6.2, 6.5), W("good", 6.6, 7.0)] },
                12500 => new StreamingResult("", ". <en-US> Done") { FinalContinuesPrevious = true, FinalWords = [W(".", 10.0, 10.0), W("Done", 10.0, 10.4)] },
                _ => new StreamingResult("", null),
            },
            new StreamingResult("", null));

        var segments = OfflineTranscriber.Run(recognizer, Audio(14), new OfflineTranscriptionOptions()).Document.Segments;

        Assert.Equal(["Reading ables", "are good.", "Done"], segments.Select(s => s.Text));
        Assert.All(segments, s => Assert.Equal(WordTimingKinds.Decoder, s.WordTiming));
        Assert.Equal(["Reading", "ables"], segments[0].Words.Select(w => w.Text));
        Assert.Equal((1.0, 6.1), (segments[0].Start, segments[0].End));                         // the word was completed by its late piece
        Assert.Equal(6.1, segments[0].Words[^1].End);
        Assert.Equal([("are", 6.2, 6.5), ("good.", 6.6, 7.0)], segments[1].Words.Select(w => (w.Text, w.Start, w.End)));
        Assert.Equal((6.2, 7.0), (segments[1].Start, segments[1].End));                         // the mark has no time of its own
        Assert.Equal([("Done", 10.0, 10.4)], segments[2].Words.Select(w => (w.Text, w.Start, w.End)));
        Assert.Equal("Reading ables are good. Done", string.Join(' ', segments.Select(s => s.Text)));
    }

    [Fact]
    public void Run_ShouldAttachAMarkThatIsStillOpenAfterTheFlushToTheLastSegment()
    {
        var recognizer = new ScriptedUtteranceRecognizer(
            fed => fed == 3500 ? new StreamingResult("", "Hello world") { FinalWords = [W("Hello", 1.0, 1.4), W("world", 1.5, 2.0)] } : new StreamingResult("", null),
            new StreamingResult(".", null) { PartialContinuesPrevious = true, PartialWords = [W(".", 9.0, 9.0)] });

        var segments = OfflineTranscriber.Run(recognizer, Audio(10), new OfflineTranscriptionOptions()).Document.Segments;

        var segment = Assert.Single(segments);
        Assert.Equal("Hello world.", segment.Text);
        Assert.Equal(["Hello", "world."], segment.Words.Select(w => w.Text));
    }

    // ── checkpoints of a recognizer with word times: in the silence in front of the segment just committed ──

    /// <summary>Two utterances: "Hello world" (1.0-2.0 s) and then <paramref name="second"/> starting at <paramref name="secondStart"/> s.</summary>
    private static ScriptedUtteranceRecognizer TwoUtterances(string second, double secondStart, bool continuesPrevious = false) =>
        new(fed => fed switch
            {
                4000 => new StreamingResult("", "Hello world") { FinalWords = [W("Hello", 1.0, 1.4), W("world", 1.5, 2.0)] },
                9000 => new StreamingResult("", second)
                {
                    FinalContinuesPrevious = continuesPrevious,
                    FinalWords = TextTokens.SplitWords(second).Select((w, i) => W(w, secondStart + 0.3 * i, secondStart + 0.3 * i + 0.25)).ToList(),
                },
                _ => new StreamingResult("", null),
            },
            new StreamingResult("", null));

    private static List<OfflineCheckpoint> Checkpoints(IUtteranceStreamingRecognizer recognizer, float[] audio)
    {
        var checkpoints = new List<OfflineCheckpoint>();
        OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions { Checkpoint = checkpoints.Add });
        return checkpoints;
    }

    [Fact]
    public void Checkpoint_ShouldLieInTheSilenceInFrontOfTheSegmentThatWasJustCommitted()
    {
        var checkpoints = Checkpoints(TwoUtterances("Next one", 6.0), Audio(10));

        // none for the first segment (nothing before it); for the second one a point between "world" (ends 2.0 s) and "Next" (6.0 s)
        var checkpoint = Assert.Single(checkpoints);
        Assert.InRange(checkpoint.Sample, 3980, 4020);                                  // the middle of the pause
        Assert.Equal("Hello world", Assert.Single(checkpoint.Segments).Text);           // the segment itself is recognized again
    }

    [Fact]
    public void Checkpoint_OfSegmentsThatFollowEachOtherClosely_ShouldStayInTheGapBetweenThem()
    {
        var checkpoints = Checkpoints(TwoUtterances("Next one", 2.15), Audio(10));

        Assert.InRange(Assert.Single(checkpoints).Sample, 2000, 2150);                  // a gap of 0.15 s
    }

    [Fact]
    public void Checkpoint_WithoutAPauseBetweenTheSegments_ShouldNotBeRaised()
    {
        Assert.Empty(Checkpoints(TwoUtterances("Next one", 2.05), Audio(10)));           // a gap of 0.05 s: nowhere to resume
    }

    [Fact]
    public void Checkpoint_ShouldHoldTheClosingMarkThatLedTheSegmentJustCommitted()
    {
        // the model wrote the "." of the first sentence together with the next speech: it belongs to the first segment, and the run
        // that resumes in front of the second one cannot produce it
        var checkpoints = Checkpoints(TwoUtterances(". Next one", 6.0, continuesPrevious: true), Audio(10));

        Assert.Equal("Hello world.", Assert.Single(Assert.Single(checkpoints).Segments).Text);
    }

    [Fact]
    public void Checkpoint_ShouldLieInTheSilentStretchOfTheAudioWhateverTheBoundsOfTheSegmentsSay()
    {
        // speech until 3.2 s and from 3.4 s; the segments claim the first ends at 2.0 s and the second starts at 5.0 s, so their middle
        // (3.5 s) is inside the second one's speech, the quietest stretch of the audio between them is 3.2-3.4 s
        var audio = Audio(10);
        for (var i = 1000; i < 3200; i++)
            audio[i] = 0.5f * (i % 2 == 0 ? 1 : -1);
        for (var i = 3400; i < 6000; i++)
            audio[i] = 0.5f * (i % 2 == 0 ? 1 : -1);

        var checkpoint = Assert.Single(Checkpoints(TwoUtterances("Next one", 5.0), audio));

        Assert.InRange(checkpoint.Sample, 3200, 3400);
    }

    [Fact]
    public void Checkpoint_WithoutWordTimes_ShouldBeTheEndOfTheBatch()
    {
        var recognizer = new ScriptedUtteranceRecognizer(
            fed => fed == 3500 ? new StreamingResult("", "Hello world.") : new StreamingResult("", null),
            new StreamingResult("", null));
        var checkpoints = new List<OfflineCheckpoint>();

        OfflineTranscriber.Run(recognizer, Audio(6), new OfflineTranscriptionOptions { Checkpoint = checkpoints.Add });

        Assert.Equal(3500, Assert.Single(checkpoints).Sample);
    }

    [Fact]
    public void Run_ShouldGroupPlainDeltasByPausesAndDetectLanguage()
    {
        var recognizer = new ScriptedPlainRecognizer(
            fed => fed switch
            {
                1200 => "<ru-RU> Привет",
                1500 => " мир.",
                4000 => " Second",
                _ => null,
            },
            " part.");
        var checkpoints = new List<OfflineCheckpoint>();

        var result = OfflineTranscriber.Run(recognizer, Audio(4.5), new OfflineTranscriptionOptions
        {
            PauseSeconds = 1.0,
            Checkpoint = checkpoints.Add,
        });

        var segments = result.Document.Segments;
        Assert.Equal(2, segments.Count);
        Assert.Equal((1.1, 1.5, "Привет мир."), (segments[0].Start, segments[0].End, segments[0].Text));
        Assert.Equal((3.9, 4.5, "Second part."), (segments[1].Start, segments[1].End, segments[1].Text));
        Assert.Equal("ru", result.DetectedLanguage);
        // Checkpoint at the pause that closed the first segment, resuming after it.
        var checkpoint = Assert.Single(checkpoints);
        Assert.Equal(2500, checkpoint.Sample);
    }

    [Fact]
    public void Run_ShouldCloseLongPlainSegmentsAtSentenceEnds()
    {
        var recognizer = new ScriptedPlainRecognizer(
            fed => fed % 500 == 0 ? (fed % 2000 == 0 ? " end." : " word") : null,
            null);

        var result = OfflineTranscriber.Run(recognizer, Audio(10), new OfflineTranscriptionOptions { MaxSegmentSeconds = 3 });

        Assert.True(result.Document.Segments.Count >= 2);
        Assert.All(result.Document.Segments.SkipLast(1), s => Assert.EndsWith("end.", s.Text));
    }

    [Fact]
    public void Run_ShouldStopOnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var recognizer = new ScriptedUtteranceRecognizer(
            fed =>
            {
                if (fed >= 2000)
                    cts.Cancel();
                return new StreamingResult("", null);
            },
            new StreamingResult("", null));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            OfflineTranscriber.Run(recognizer, Audio(10), cancellationToken: cts.Token));
        Assert.True(recognizer.Fed < 10000);
    }

    [Fact]
    public void Run_ShouldMoveLateSentencePunctuationToThePreviousSegment()
    {
        var recognizer = new ScriptedUtteranceRecognizer(
            fed => fed switch
            {
                2000 => new StreamingResult("", "She'll be alright"),
                4000 => new StreamingResult("", ". Six"),
                6000 => new StreamingResult("", "?"),
                _ => new StreamingResult("", null),
            },
            new StreamingResult("", null));

        var result = OfflineTranscriber.Run(recognizer, Audio(7));

        Assert.Equal(["She'll be alright.", "Six?"], result.Document.Segments.Select(s => s.Text));
        Assert.Equal("alright.", result.Document.Segments[0].Words[^1].Text);
    }

    [Fact]
    public void Run_ShouldSnapBoundsToSpeechEnergy()
    {
        // 16 kHz audio: noise floor, speech bursts 1.0–2.5 s and 4.0–5.0 s.
        const int rate = 16000;
        var audio = new float[rate * 7];
        var random = new Random(1);
        for (var i = 0; i < audio.Length; i++)
        {
            var t = (double)i / rate;
            var speech = t is >= 1.0 and < 2.5 or >= 4.0 and < 5.0;
            audio[i] = (float)((random.NextDouble() - 0.5) * 0.002 + (speech ? 0.3 * Math.Sin(2 * Math.PI * 220 * t) : 0));
        }
        // The recognizer reports the first utterance late (partial at 1.8 s, commit at 3.6 s with
        // 0.8 s EOU → estimate 1.8–2.8 s) and the second one at 4.4–5.9 s.
        var recognizer = new RateRecognizer(rate, fed => fed switch
        {
            > (long)(1.8 * rate) and < (long)(3.6 * rate) => new StreamingResult("one two", null),
            (long)(3.6 * rate) => new StreamingResult("", "One two three."),
            > (long)(4.4 * rate) and < (long)(6.6 * rate) => new StreamingResult("four", null),
            (long)(6.6 * rate) => new StreamingResult("", "Four five."),
            _ => new StreamingResult("", null),
        });

        var segments = OfflineTranscriber.Run(recognizer, audio).Document.Segments;

        Assert.Equal(2, segments.Count);
        Assert.InRange(segments[0].Start, 0.95, 1.05);
        Assert.InRange(segments[0].End, 2.45, 2.55);
        Assert.InRange(segments[1].Start, 3.95, 4.05);
        Assert.InRange(segments[1].End, 4.95, 5.05);
        Assert.Equal(segments[0].Start, segments[0].Words[0].Start);
        Assert.Equal(segments[0].End, segments[0].Words[^1].End);

        var unrefined = OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions { RefineBounds = false }).Document.Segments;
        Assert.InRange(unrefined[0].Start, 1.7, 1.9);
    }

    private sealed class RateRecognizer(int rate, Func<long, StreamingResult> step) : IUtteranceStreamingRecognizer
    {
        private long _fed;
        public int SampleRate => rate;
        public int ChunkSamples => rate / 10;
        public double StopHistoryEouSeconds => 0.8;
        public StreamingResult ProcessUtterance(float[] chunk) { _fed += chunk.Length; return step(_fed); }
        public StreamingResult FlushUtterance() => new("", null);
        public string? ProcessAudio(float[] chunk) => throw new NotSupportedException();
        public string? Flush() => throw new NotSupportedException();
        public void ResetStreamingState() => _fed = 0;
        public void Dispose() { }
    }

    [Fact]
    public void PartFile_ShouldRoundTripCheckpointSegments()
    {
        var path = OfflinePartFile.PathFor(_directory.Path, "j1");
        var segments = new List<TranscriptSegment>
        {
            new() { Index = 0, Start = 0.5, End = 2, Text = "One two", Words = [new("One", 0.5, 1), new("two", 1, 2)] },
        };

        OfflinePartFile.Save(path, new OfflineCheckpoint(32000, segments));
        var loaded = OfflinePartFile.TryLoad(path);

        Assert.NotNull(loaded);
        Assert.Equal(32000, loaded.Sample);
        Assert.Equal("One two", Assert.Single(loaded.Segments).Text);
        Assert.Equal(2, loaded.Segments[0].Words.Count);

        File.WriteAllText(path, """{ "schema": 1, "segments": [] }""");
        Assert.Null(OfflinePartFile.TryLoad(path)); // no sample: start over

        OfflinePartFile.Delete(path);
        Assert.Null(OfflinePartFile.TryLoad(path));
        Assert.Null(OfflinePartFile.TryLoad(null));
    }
}
