using SpeechLib.Diarization;
using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;

namespace VoiceType.Uno.Tests.History;

/// <summary>Recordings with a scripted conversation, runs and layers for the speaker tests.</summary>
internal sealed class SpeakerFixtures : IDisposable
{
    public SpeakerFixtures()
    {
        Directory = new TempDirectory();
        Clock = new ManualTimeProvider(HistoryFixtures.T0);
        Store = new RecordingStore(Path.Combine(Directory.Path, "sessions"), Clock);
    }

    public TempDirectory Directory { get; }
    public ManualTimeProvider Clock { get; }
    public RecordingStore Store { get; }

    public void Dispose() => Directory.Dispose();

    /// <summary>A turn: (start, end, channel).</summary>
    public static (double Start, double End, int Speaker) Turn(double start, double end, int speaker) => (start, end, speaker);

    /// <summary>
    /// Segments of four seconds, each with ten words timed evenly (estimated) or as the decoder would
    /// (<paramref name="decoder"/>); <see cref="Store"/> gets the recording, finished, with version v1 current.
    /// </summary>
    public string CreateRecording(int segments = 6, bool decoder = false, double segmentSeconds = 4)
    {
        var entry = Store.CreateRecording(HistoryFixtures.T0);
        var doc = new TranscriptDocument();
        for (var i = 0; i < segments; i++)
        {
            var start = i * segmentSeconds;
            var end = start + segmentSeconds - 0.4;
            var text = string.Join(' ', Enumerable.Range(0, 10).Select(w => $"w{i}x{w}"));
            doc.Segments.Add(new TranscriptSegment
            {
                Index = i,
                Start = start,
                End = end,
                Text = text,
                Words = TranscriptTimings.Distribute(TextTokens.SplitWords(text), start, end),
                WordTiming = decoder ? WordTimingKinds.Decoder : WordTimingKinds.Estimated,
            });
        }
        doc.Text = TranscriptDocument.JoinSegments(doc.Segments);
        Store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), doc);
        Store.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(5),
            new AudioInfo { File = "audio.wav", Codec = "pcm16", SampleRate = 16000, DurationSec = segments * segmentSeconds });
        return entry.Id;
    }

    public static DiarizationRunDocument Run(double duration, params (double Start, double End, int Speaker)[] turns)
    {
        var rows = turns.Select(t => new RunTurn { Start = t.Start, End = t.End, Speaker = t.Speaker }).ToList();
        var timeline = RunProbabilityCodec.FromTurns(rows, duration);
        return new DiarizationRunDocument
        {
            DurationSec = duration,
            Turns = rows,
            Probs = RunProbabilityCodec.Pack(timeline),
        };
    }

    public static DiarizationRunInfo RunSpec(string? jobId = null) => new()
    {
        Source = DiarizationSources.Job,
        JobId = jobId,
        Model = new DiarizationModelInfo { Folder = "nemotron-3-diarization-onnx-int8", Title = "Nemotron 3 Diarization INT8", Precision = "Int8", Family = "Sortformer" },
        Mode = new DiarizationModeInfo { Preset = "offline", LatencySec = 30.4 },
        Post = new DiarizationPostInfo(),
    };

    public DiarizationRunInfo AddRun(string recordingId, double duration, params (double Start, double End, int Speaker)[] turns) =>
        Store.AddDiarizationRun(recordingId, RunSpec(), Run(duration, turns));

    public SpeakerLayerInfo Label(string recordingId, string runId, string versionId = "v1")
    {
        var built = SpeakerLayerBuilder.Build(Store.LoadDiarizationRun(recordingId, runId), Store.LoadTranscript(recordingId, versionId), versionId);
        return Store.SaveSpeakerLayer(recordingId,
            new SpeakerLayerInfo { Version = versionId, Run = runId, WordTiming = built.WordTiming }, built.Document);
    }

    /// <summary>Two speakers alternating every segment: 0 on even segments, 1 on odd ones.</summary>
    public static (double Start, double End, int Speaker)[] Alternating(int segments, double segmentSeconds = 4) =>
        Enumerable.Range(0, segments)
            .Select(i => (i * segmentSeconds, (i + 1) * segmentSeconds, i % 2))
            .ToArray();
}
