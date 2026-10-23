using System.Buffers.Binary;
using SpeechLib.Diarization;
using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>The Diarize and Analyze job workers with a scripted diarizer (no model files needed).</summary>
public class DiarizeJobWorkerTests : IDisposable
{
    private readonly SpeakerFixtures _f = new();
    private readonly FakeDiarizerFactory _factory = new();
    private HistorySettings _settings = new();

    public void Dispose() => _f.Dispose();

    private sealed class Sink : IJobProgressSink
    {
        public List<double> Progress { get; } = [];
        public List<string?> Details { get; } = [];
        public void Detail(string? detail) => Details.Add(detail);
        void IJobProgressSink.Progress(double fraction) => Progress.Add(fraction);
        public void Checkpoint(JobCheckpoint checkpoint) { }
    }

    /// <summary>A 16 kHz PCM16 mono WAV next to the manifest of a recording made by <see cref="SpeakerFixtures"/>.</summary>
    private string RecordingWithAudio(int segments = 6, bool decoder = true)
    {
        var id = _f.CreateRecording(segments, decoder);
        var folder = _f.Store.TryGetFolder(id)!;
        WriteWav(Path.Combine(folder, "audio.wav"), segments * 4, 16000);
        return id;
    }

    private static void WriteWav(string path, double seconds, int rate)
    {
        var samples = (int)(seconds * rate);
        var bytes = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + samples * 2);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2);
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), (short)(1000 * Math.Sin(i * 0.05)));
        File.WriteAllBytes(path, bytes);
    }

    private DiarizeJobWorker Worker(bool phone = false) =>
        new(_f.Store, _factory, () => _settings, phone, () => new AppInfo { Name = "test", Version = "1", Platform = "win-x64" });

    private static HistoryJob DiarizeJob(string recordingId, string? version = null, string id = "j1", DiarizationJobParameters? parameters = null) => new()
    {
        Id = id,
        Kind = HistoryJobKind.Diarize,
        RecordingId = recordingId,
        Parameters = new JobParameters { SourceVersion = version, Diarization = parameters ?? new DiarizationJobParameters() },
    };

    [Fact]
    public async Task Diarize_ShouldStoreTheRunAndLabelTheVersion()
    {
        var id = RecordingWithAudio();
        var sink = new Sink();

        var result = await Worker().RunAsync(DiarizeJob(id), sink, CancellationToken.None);

        Assert.Equal("s1", result);
        var manifest = _f.Store.Get(id)!.Manifest;
        var run = Assert.Single(manifest.DiarizationRuns);
        Assert.Equal("j1", run.JobId);
        Assert.Equal(DiarizationSources.Job, run.Source);
        Assert.Equal("nemotron-3-diarization-onnx-int8", run.Model!.Folder);
        Assert.Equal("Nemotron 3 Diarization INT8", run.Model.Title);
        Assert.Equal("nvidia/Nemotron-3-Diarization", run.Model.Upstream);
        Assert.Equal("offline", run.Mode!.Preset);
        Assert.Equal(0.5, run.Post!.Threshold);
        Assert.Equal(2, run.Stats!.Speakers);
        Assert.Equal("test", run.App!.Name);
        var layer = Assert.Single(manifest.SpeakerLayers);
        Assert.Equal(WordTimingKinds.Decoder, layer.WordTiming);
        Assert.Equal("s1", manifest.CurrentSpeakers["v1"]);
        Assert.Equal(["p1", "p2"], manifest.Speakers!.People.Select(p => p.Id));
        Assert.True(sink.Progress.Count > 1);
        Assert.Equal(1.0, sink.Progress[^1]);
        Assert.Equal(sink.Progress.Order(), sink.Progress);
        Assert.Equal(24 * 16000, _factory.SamplesFed);
        Assert.Equal(1, _factory.Disposed); // the model is unloaded when the job ends
    }

    [Fact]
    public async Task Diarize_ShouldFeedBoundedBlocks()
    {
        var id = RecordingWithAudio(segments: 12);

        await Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None);

        Assert.True(_factory.Blocks >= 9);
        Assert.True(_factory.LargestBlock <= 5 * 16000);
    }

    [Fact]
    public async Task Diarize_ShouldFlagEstimatedWordTimes()
    {
        var id = RecordingWithAudio(decoder: false);

        await Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None);

        Assert.Equal(WordTimingKinds.Estimated, _f.Store.Get(id)!.Manifest.SpeakerLayers[0].WordTiming);
    }

    [Fact]
    public async Task Diarize_ShouldFailClearlyWithoutTheModel()
    {
        var id = RecordingWithAudio();
        _factory.Installed = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None));

        Assert.Contains("not installed", error.Message);
        Assert.Empty(_f.Store.Get(id)!.Manifest.DiarizationRuns);
    }

    [Fact]
    public async Task Diarize_ShouldFailWhenTheAudioWasDeleted()
    {
        var id = _f.CreateRecording();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None));

        Assert.Contains("audio", error.Message);
    }

    [Fact]
    public async Task Diarize_ShouldWriteNothingWhenCancelled()
    {
        var id = RecordingWithAudio();
        using var cts = new CancellationTokenSource();
        _factory.OnBlock = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Worker().RunAsync(DiarizeJob(id), new Sink(), cts.Token));

        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Empty(manifest.DiarizationRuns);
        Assert.Empty(manifest.SpeakerLayers);
        Assert.Equal(1, _factory.Disposed);
        Assert.False(Directory.Exists(Path.Combine(_f.Store.TryGetFolder(id)!, "diarization")));
    }

    [Fact]
    public async Task Diarize_RestartAfterAKilledAppShouldReuseTheRunStoredForTheSameJob()
    {
        var id = RecordingWithAudio();
        // The previous process stored the run and died before the layer was written.
        _f.Store.AddDiarizationRun(id, SpeakerFixtures.RunSpec(jobId: "j1"), SpeakerFixtures.Run(24, SpeakerFixtures.Alternating(6)));

        var result = await Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None);

        Assert.Equal("s1", result);
        Assert.Equal(0, _factory.Created);
        Assert.Single(_f.Store.Get(id)!.Manifest.DiarizationRuns);
    }

    [Fact]
    public async Task Diarize_ShouldReuseAnEarlierRunOfTheSameModelAndPresetForAnotherVersion()
    {
        var id = RecordingWithAudio();
        await Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None);
        var edited = _f.Store.SaveEdit(id, "v1", Enumerable.Range(0, 6).Select(i => $"edited text {i} now").ToList());

        var result = await Worker().RunAsync(DiarizeJob(id, version: edited.Id, id: "j2"), new Sink(), CancellationToken.None);

        Assert.Equal("s2", result);
        Assert.Equal(1, _factory.Created); // the model ran once
        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Single(manifest.DiarizationRuns);
        Assert.Equal("r1", manifest.CurrentSpeakerLayer("v2")!.Run);
    }

    [Fact]
    public async Task Diarize_ShouldRunAgainWhenReuseIsOffOrThePresetDiffers()
    {
        var id = RecordingWithAudio();
        await Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None);

        await Worker().RunAsync(DiarizeJob(id, id: "j2", parameters: new DiarizationJobParameters { ReuseExistingRun = false }), new Sink(), CancellationToken.None);
        await Worker().RunAsync(DiarizeJob(id, id: "j3", parameters: new DiarizationJobParameters { Preset = "low_latency" }), new Sink(), CancellationToken.None);

        Assert.Equal(3, _f.Store.Get(id)!.Manifest.DiarizationRuns.Count);
        Assert.Equal("low_latency", _f.Store.Get(id)!.Manifest.DiarizationRuns[2].Mode!.Preset);
    }

    [Fact]
    public async Task Diarize_ShouldLabelTheVersionOfTheJobNotTheCurrentOne()
    {
        var id = RecordingWithAudio();
        var v2 = _f.Store.AddVersion(id, HistoryFixtures.LiveSpec(), _f.Store.LoadTranscript(id, "v1"), makeCurrent: false);

        await Worker().RunAsync(DiarizeJob(id, version: v2.Id), new Sink(), CancellationToken.None);

        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Equal("v1", manifest.CurrentVersion);
        Assert.Equal("v2", Assert.Single(manifest.SpeakerLayers).Version);
        Assert.False(manifest.CurrentSpeakers.ContainsKey("v1"));
    }

    [Fact]
    public async Task Diarize_ShouldPickThreadsByPlatformAndSetting()
    {
        var id = RecordingWithAudio();

        await Worker(phone: false).RunAsync(DiarizeJob(id, parameters: new DiarizationJobParameters { ReuseExistingRun = false }), new Sink(), CancellationToken.None);
        await Worker(phone: true).RunAsync(DiarizeJob(id, id: "j2", parameters: new DiarizationJobParameters { ReuseExistingRun = false }), new Sink(), CancellationToken.None);
        _settings.Speakers.Threads = 3;
        await Worker(phone: true).RunAsync(DiarizeJob(id, id: "j3", parameters: new DiarizationJobParameters { ReuseExistingRun = false }), new Sink(), CancellationToken.None);

        Assert.Equal([4, 2, 3], _factory.Options.Select(o => o.Threads));
        Assert.All(_factory.Options, o => Assert.Equal("offline", o.Preset));
    }

    [Fact]
    public async Task Diarize_ShouldStoreOnlyTheRunForARecordingWithoutTranscript()
    {
        var entry = _f.Store.CreateRecording(HistoryFixtures.T0);
        _f.Store.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(1), new AudioInfo { File = "audio.wav", DurationSec = 24 });
        WriteWav(Path.Combine(_f.Store.TryGetFolder(entry.Id)!, "audio.wav"), 24, 16000);

        var result = await Worker().RunAsync(DiarizeJob(entry.Id), new Sink(), CancellationToken.None);

        Assert.Equal("r1", result);
        Assert.Empty(_f.Store.Get(entry.Id)!.Manifest.SpeakerLayers);
    }

    [Fact]
    public async Task Diarize_ShouldResampleAudioThatIsNotSixteenKilohertz()
    {
        var id = _f.CreateRecording();
        WriteWav(Path.Combine(_f.Store.TryGetFolder(id)!, "audio.wav"), 24, 48000);

        await Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None);

        Assert.InRange(_factory.SamplesFed, 24 * 16000 - 800, 24 * 16000 + 800);
    }

    // ── Analyze ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_ShouldWriteSpeakerStatsAndSkipGemmaWhenItIsNotInstalled()
    {
        var id = RecordingWithAudio();
        await Worker().RunAsync(DiarizeJob(id), new Sink(), CancellationToken.None);
        var job = new HistoryJob { Id = "j9", Kind = HistoryJobKind.Analyze, RecordingId = id, Parameters = new JobParameters { SourceVersion = "v1", Analysis = new AnalysisJobParameters() } };
        var sink = new Sink();

        var result = await new AnalyzeJobWorker(_f.Store, () => null, _f.Clock).RunAsync(job, sink, CancellationToken.None);

        Assert.Equal("a1", result);
        var manifest = _f.Store.Get(id)!.Manifest;
        var analysis = Assert.Single(manifest.Analyses);
        Assert.Equal("analysis/v1.s1.insights.json", analysis.File);
        Assert.Equal("s1", analysis.SpeakerLayer);
        Assert.Equal(2, analysis.NamesSnapshot!.Count);
        var insights = _f.Store.LoadInsights(id, "a1")!;
        Assert.Equal(30, insights.Stats["0"].Words);
        Assert.Contains(sink.Details, d => d?.Contains("Gemma") == true);
        Assert.Equal(1.0, sink.Progress[^1]);
    }

    [Fact]
    public async Task Analyze_ShouldRunSummaryAndTitleOnTheChainsVersionWhenGemmaIsThere()
    {
        var id = RecordingWithAudio();
        var v2 = _f.Store.AddVersion(id, HistoryFixtures.LiveSpec(), _f.Store.LoadTranscript(id, "v1"), makeCurrent: false);
        await Worker().RunAsync(DiarizeJob(id, version: v2.Id), new Sink(), CancellationToken.None);
        var host = new FakeLlmHost
        {
            Responder = (r, _) => r.Messages[0].Text.StartsWith("You summarize", StringComparison.Ordinal) ? "Chunk summary [00:05]." : "Weekly sync",
        };
        var job = new HistoryJob { Id = "j9", Kind = HistoryJobKind.Analyze, RecordingId = id, Parameters = new JobParameters { SourceVersion = v2.Id } };

        await new AnalyzeJobWorker(_f.Store, () => host, _f.Clock).RunAsync(job, new Sink(), CancellationToken.None);

        Assert.True(host.Calls > 0);
        var analysis = new AnalysisStore(_f.Store);
        Assert.NotNull(analysis.LoadSummary(id, "v2"));
        Assert.Null(analysis.LoadSummary(id, "v1"));
        Assert.Equal("Weekly sync", _f.Store.Get(id)!.Manifest.AutoTitle);
        Assert.Single(_f.Store.Get(id)!.Manifest.Analyses);
    }

    // ── fake model ──────────────────────────────────────────────────────

    private sealed class FakeDiarizerFactory : IDiarizerFactory
    {
        public bool Installed { get; set; } = true;
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public long SamplesFed { get; private set; }
        public int Blocks { get; private set; }
        public int LargestBlock { get; private set; }
        public Action? OnBlock { get; set; }
        public List<DiarizerRunOptions> Options { get; } = [];

        public DiarizerModelRef? Find(string? preferredFolder = null) =>
            Installed ? new DiarizerModelRef(Path.Combine(Path.GetTempPath(), "models", "nemotron-3-diarization-onnx-int8"),"Nemotron 3 Diarization INT8", "Int8", 105_852_942) : null;

        public IJobDiarizer Create(DiarizerModelRef model, DiarizerRunOptions options)
        {
            Created++;
            Options.Add(options);
            return new Job(this, options);
        }

        private sealed class Job(FakeDiarizerFactory owner, DiarizerRunOptions options) : IJobDiarizer
        {
            public DiarizerRunResult Diarize(IEnumerable<ReadOnlyMemory<float>> blocks, double totalSeconds, IProgress<double>? progress, CancellationToken cancellationToken)
            {
                long samples = 0;
                foreach (var block in blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    samples += block.Length;
                    owner.Blocks++;
                    owner.LargestBlock = Math.Max(owner.LargestBlock, block.Length);
                    progress?.Report(samples / 16000.0 / totalSeconds);
                    owner.OnBlock?.Invoke();
                }
                cancellationToken.ThrowIfCancellationRequested();
                owner.SamplesFed = samples;
                progress?.Report(1);

                var seconds = samples / 16000.0;
                var turns = new List<SpeakerTurn>();
                for (var t = 0.0; t < seconds; t += 4)
                    turns.Add(new SpeakerTurn(t, Math.Min(t + 4, seconds), (int)(t / 4) % 2));
                var rows = turns.Select(t => new RunTurn { Start = t.Start, End = t.End, Speaker = t.Speaker }).ToList();
                var timeline = RunProbabilityCodec.FromTurns(rows, seconds);
                var mode = new DiarizationModeInfo { Preset = options.Preset, LatencySec = 30.4, ChunkLen = 340, FifoLen = 40 };
                return new DiarizerRunResult(turns, timeline, seconds, 0.5, mode, "Cpu");
            }

            public void Dispose() => owner.Disposed++;
        }
    }
}
