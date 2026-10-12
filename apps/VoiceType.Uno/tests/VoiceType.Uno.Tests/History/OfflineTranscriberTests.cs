using SpeechLib;
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
