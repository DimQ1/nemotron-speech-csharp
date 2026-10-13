using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.AudioStorage;
using SpeechLib.Diarization;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.History;

/// <summary>A diarization model that is installed (found on disk, validated).</summary>
/// <param name="Folder">Full folder path.</param>
/// <param name="Title">"Nemotron 3 Diarization INT8".</param>
/// <param name="Precision">"Int8" | "Fp32".</param>
/// <param name="Bytes">Size of the files the variant needs.</param>
public sealed record DiarizerModelRef(string Folder, string Title, string Precision, long Bytes)
{
    /// <summary>Folder name, e.g. <c>nemotron-3-diarization-onnx-int8</c>.</summary>
    public string FolderName => Path.GetFileName(Folder.TrimEnd('/', '\\'));
}

/// <summary>How a run is made.</summary>
/// <param name="Preset"><see cref="DiarizationPreset"/> key of <c>diar_config.json</c> (<c>offline</c>).</param>
public sealed record DiarizerRunOptions(
    string Preset, int Threads, string? ExecutionProvider, PostProcessingOptions Post);

/// <summary>Output of one run, in terms the History data model stores.</summary>
public sealed record DiarizerRunResult(
    IReadOnlyList<SpeakerTurn> Turns,
    SpeakerActivityTimeline Timeline,
    double DurationSeconds,
    double ElapsedSeconds,
    DiarizationModeInfo Mode,
    string ExecutionProvider);

/// <summary>A loaded model ready to diarize one recording. Disposing unloads the model.</summary>
public interface IJobDiarizer : IDisposable
{
    /// <summary>Diarizes 16 kHz mono audio that arrives in blocks (memory does not grow with the recording).</summary>
    DiarizerRunResult Diarize(IEnumerable<ReadOnlyMemory<float>> blocks, double totalSeconds, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Seam between the Diarize job and the ONNX model, so the job is testable without the model files.</summary>
public interface IDiarizerFactory
{
    /// <summary>The installed model to use (<paramref name="preferredFolder"/> when given and installed), or null when none is.</summary>
    DiarizerModelRef? Find(string? preferredFolder = null);

    /// <summary>Loads the model. One instance per job; the caller disposes it.</summary>
    IJobDiarizer Create(DiarizerModelRef model, DiarizerRunOptions options);
}

/// <summary>
/// <see cref="IDiarizerFactory"/> over <c>SpeechLib.Diarization</c>: finds <c>nemotron-3-diarization-onnx-int8|-fp32|bare</c>
/// under the given roots (<see cref="DiarizationModelLocator.Find"/>) and runs <see cref="OfflineDiarizer"/>.
/// </summary>
/// <remarks>
/// The app passes <see cref="ModelStorageLayout.DiarizationScanRoots"/> (<c>&lt;models root&gt;/diarization</c> first, then the flat
/// root of older versions); Models › Speakers downloads the catalog models (<see cref="DiarizationModelCatalog"/>) into the first.
/// </remarks>
public sealed class OnnxDiarizerFactory(Func<IReadOnlyList<string>> modelRoots) : IDiarizerFactory
{
    public DiarizerModelRef? Find(string? preferredFolder = null)
    {
        var roots = modelRoots();
        DiarizationModelDescriptor? descriptor = null;
        if (!string.IsNullOrWhiteSpace(preferredFolder))
        {
            foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                var folder = Path.Combine(root, preferredFolder);
                if (Directory.Exists(folder) && DiarizationModelLocator.TryValidate(folder, DiarizationPrecision.Auto, out var d, out _))
                {
                    descriptor = d;
                    break;
                }
            }
        }
        descriptor ??= DiarizationModelLocator.Find(roots);
        if (descriptor is null)
            return null;
        var precision = descriptor.Precision == DiarizationPrecision.Int8 ? "INT8" : "FP32";
        return new DiarizerModelRef(descriptor.Folder, "Nemotron 3 Diarization " + precision, descriptor.Precision.ToString(), descriptor.TotalBytes);
    }

    public IJobDiarizer Create(DiarizerModelRef model, DiarizerRunOptions options)
    {
        var preset = ParsePreset(options.Preset);
        var precision = model.Precision == nameof(DiarizationPrecision.Fp32) ? DiarizationPrecision.Fp32 : DiarizationPrecision.Int8;
        var loaded = DiarizationModel.Load(new DiarizationModelOptions
        {
            ModelFolder = model.Folder,
            Precision = precision,
            Threads = options.Threads,
            ExecutionProvider = options.ExecutionProvider,
        });
        try
        {
            var diarizer = new OfflineDiarizer(loaded, new OfflineDiarizerOptions { Preset = preset, PostProcessing = options.Post, KeepProbabilities = true });
            return new Job(loaded, diarizer, preset);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    /// <summary>The preset of a config key (<c>offline</c>, <c>low_latency</c>…); unknown → offline.</summary>
    public static DiarizationPreset ParsePreset(string? key)
    {
        foreach (var preset in Enum.GetValues<DiarizationPreset>())
        {
            if (string.Equals(DiarizationConfig.ConfigKey(preset), key, StringComparison.OrdinalIgnoreCase))
                return preset;
        }
        return DiarizationPreset.Offline;
    }

    private sealed class Job(DiarizationModel model, OfflineDiarizer diarizer, DiarizationPreset preset) : IJobDiarizer
    {
        public DiarizerRunResult Diarize(IEnumerable<ReadOnlyMemory<float>> blocks, double totalSeconds, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            var result = diarizer.Diarize(blocks, totalSeconds, progress, cancellationToken);
            var g = model.Config.GetGeometry(preset);
            var mode = new DiarizationModeInfo
            {
                Preset = DiarizationConfig.ConfigKey(preset),
                LatencySec = Math.Round(g.BufferLatencySeconds, 2),
                SpkcacheLen = model.Config.Aosc.CacheLen,
                FifoLen = g.FifoLen,
                ChunkLen = g.ChunkLen,
                RightContext = g.RightContext,
                UpdatePeriod = g.UpdatePeriod,
            };
            return new DiarizerRunResult(result.Turns, result.Timeline ?? new SpeakerActivityTimeline(),
                result.DurationSeconds, result.ElapsedSeconds, mode, model.ExecutionProvider);
        }

        public void Dispose()
        {
            diarizer.Dispose();
            model.Dispose();
        }
    }
}

/// <summary>
/// <c>diarize</c> job (design §10, §11.2): decodes the stored audio block by block, runs the diarization model once (one
/// model instance, unloaded when the job ends), stores the run (<c>diarization/rM.json</c>), labels the words of the version
/// with <see cref="SpeakerLayerBuilder"/> and makes the new layer the version's current one.
/// <list type="bullet">
/// <item>Cancel and pause stop the model at the next 5 s block; nothing is written. There is no mid-run checkpoint (the run is
/// fast: RTF about 0.04): an interrupted job starts again, except that a run already stored for this job (the app was killed
/// between the two writes) is reused, and so is any earlier run of the same model and preset (<c>reuseExistingRun</c>), in which
/// case only the layer is built, in milliseconds.</item>
/// <item>The version is <see cref="JobParameters.SourceVersion"/> (set by the previous step of a chain), else the current one.</item>
/// </list>
/// </summary>
public sealed class DiarizeJobWorker
{
    /// <summary>Audio decoded and fed per call: progress and cancellation granularity.</summary>
    private const double BlockSeconds = 5;

    private readonly RecordingStore _store;
    private readonly IDiarizerFactory _factory;
    private readonly Func<HistorySettings> _settings;
    private readonly bool _isPhone;
    private readonly Func<AppInfo> _app;

    public DiarizeJobWorker(
        RecordingStore store, IDiarizerFactory factory, Func<HistorySettings> settings, bool isPhone = false, Func<AppInfo>? app = null)
    {
        _store = store;
        _factory = factory;
        _settings = settings;
        _isPhone = isPhone;
        _app = app ?? CurrentApp;
    }

    /// <returns>The id of the new speaker layer (<c>s1</c>), or of the run when the recording has no transcript.</returns>
    public Task<string?> RunAsync(HistoryJob job, IJobProgressSink sink, CancellationToken cancellationToken) =>
        Task.FromResult(Run(job, sink, cancellationToken));

    private string? Run(HistoryJob job, IJobProgressSink sink, CancellationToken cancellationToken)
    {
        var entry = _store.Get(job.RecordingId) ?? throw new InvalidOperationException("The recording no longer exists.");
        var manifest = entry.Manifest;
        if (manifest.State == RecordingStates.Recording)
            throw new InvalidOperationException("The recording is still being recorded.");

        var parameters = job.Parameters.Diarization ?? new DiarizationJobParameters();
        var settings = _settings();
        var model = _factory.Find(parameters.ModelFolder ?? settings.Speakers.ModelFolder)
            ?? throw new InvalidOperationException("The speaker detection model is not installed. Download it on the Models page, then retry.");

        var versionId = job.Parameters.SourceVersion ?? manifest.CurrentVersion;
        if (versionId is not null && manifest.FindVersion(versionId) is null)
            throw new InvalidOperationException($"Transcript {versionId} no longer exists.");

        var preset = string.IsNullOrWhiteSpace(parameters.Preset) ? "offline" : parameters.Preset!;
        var run = FindRun(manifest, job, model, preset, parameters.ReuseExistingRun != false);
        if (run is null)
        {
            var audioPath = _store.GetAudioPath(job.RecordingId)
                ?? throw new InvalidOperationException("The audio of this recording was deleted; only the text is left.");
            run = RunModel(job, sink, model, parameters, preset, audioPath, settings, cancellationToken);
        }
        else
        {
            sink.Progress(0.95);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (versionId is null)
        {
            sink.Progress(1);
            return run.Id;
        }

        sink.Detail("Labelling the transcript…");
        var layer = BuildLayer(job.RecordingId, run.Id, versionId);
        sink.Detail(null);
        sink.Progress(1);
        return layer.Id;
    }

    /// <summary>A stored run that can stand in for a new one: this job's own, or the same model and preset.</summary>
    private static DiarizationRunInfo? FindRun(RecordingManifest manifest, HistoryJob job, DiarizerModelRef model, string preset, bool reuse)
    {
        var own = manifest.DiarizationRuns.LastOrDefault(r => r.JobId == job.Id);
        if (own is not null)
            return own;
        if (!reuse)
            return null;
        return manifest.DiarizationRuns.LastOrDefault(r =>
            r.Source == DiarizationSources.Job
            && string.Equals(r.Model?.Folder, model.FolderName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Model?.Precision, model.Precision, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Mode?.Preset, preset, StringComparison.OrdinalIgnoreCase)
            && r.Post?.Threshold == (job.Parameters.Diarization?.Threshold ?? r.Post?.Threshold));
    }

    private DiarizationRunInfo RunModel(
        HistoryJob job, IJobProgressSink sink, DiarizerModelRef model, DiarizationJobParameters parameters, string preset,
        string audioPath, HistorySettings settings, CancellationToken cancellationToken)
    {
        var post = new PostProcessingOptions { Threshold = (float)(parameters.Threshold ?? PostProcessingOptions.Default.Threshold) };
        var threads = settings.Speakers.EffectiveThreads(_isPhone);
        var options = new DiarizerRunOptions(preset, threads, ExecutionProvider: null, post);

        sink.Detail("Loading " + model.Title + "…");
        cancellationToken.ThrowIfCancellationRequested();
        using var diarizer = _factory.Create(model, options);
        cancellationToken.ThrowIfCancellationRequested();

        sink.Detail("Detecting speakers…");
        using var audio = RecordingAudioStream.Open(audioPath, DiarizationConfig.SampleRate);
        var total = audio.Length / (double)audio.SampleRate;
        var progress = new Progress95(sink);
        var result = diarizer.Diarize(Blocks(audio, cancellationToken), total, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        sink.Detail("Saving…");
        var document = new DiarizationRunDocument
        {
            DurationSec = Math.Round(result.DurationSeconds, 2),
            Turns = result.Turns.Select(t => new RunTurn
            {
                Start = Math.Round(t.Start, 3),
                End = Math.Round(t.End, 3),
                Speaker = t.Speaker,
                OverlapSec = t.OverlapSeconds > 0 ? Math.Round(t.OverlapSeconds, 3) : null,
            }).ToList(),
            Probs = RunProbabilityCodec.Pack(result.Timeline),
        };
        var spec = new DiarizationRunInfo
        {
            Source = DiarizationSources.Job,
            JobId = job.Id,
            Model = new DiarizationModelInfo
            {
                CatalogRepoId = parameters.CatalogRepoId,
                Upstream = DiarizationModelLocator.UpstreamRepo,
                Folder = model.FolderName,
                Title = model.Title,
                Precision = model.Precision,
                Family = "Sortformer",
            },
            Mode = result.Mode,
            Post = new DiarizationPostInfo
            {
                Threshold = post.Threshold,
                MinSpeechSec = post.MinTurnSeconds,
                MinSilenceSec = post.MergeGapSeconds,
                MaxSpeakers = parameters.MaxSpeakers ?? DiarizationConfig.NumSpeakers,
            },
            Settings = JsonValues.Settings(("executionProvider", result.ExecutionProvider), ("threads", threads)),
            App = _app(),
            Stats = new DiarizationRunStats
            {
                OverlapSec = Math.Round(result.Turns.Sum(t => t.OverlapSeconds), 2),
                Rtf = result.DurationSeconds > 0 ? Math.Round(result.ElapsedSeconds / result.DurationSeconds, 3) : null,
                ElapsedSec = Math.Round(result.ElapsedSeconds, 1),
            },
        };
        return _store.AddDiarizationRun(job.RecordingId, spec, document);
    }

    /// <summary>Builds and stores the layer of <paramref name="versionId"/> from a stored run and makes it current.</summary>
    public SpeakerLayerInfo BuildLayer(string recordingId, string runId, string versionId, bool makeCurrent = true)
    {
        var run = _store.LoadDiarizationRun(recordingId, runId);
        var transcript = _store.LoadTranscript(recordingId, versionId);
        var built = SpeakerLayerBuilder.Build(run, transcript, versionId);
        return _store.SaveSpeakerLayer(recordingId, new SpeakerLayerInfo
        {
            Version = versionId,
            Run = runId,
            Assigner = SpeakerLayerBuilder.AssignerVersion,
            WordTiming = built.WordTiming,
        }, built.Document, makeCurrent);
    }

    private static IEnumerable<ReadOnlyMemory<float>> Blocks(RecordingAudioStream audio, CancellationToken cancellationToken)
    {
        var rate = audio.SampleRate;
        var block = new float[(int)(BlockSeconds * rate)];
        StreamingResampler? resampler = rate == DiarizationConfig.SampleRate ? null : new StreamingResampler(rate, DiarizationConfig.SampleRate);
        var output = resampler is null ? [] : new float[resampler.MaxOutputCount(block.Length)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = audio.Read(block);
            if (n == 0)
                break;
            if (resampler is null)
            {
                yield return block.AsMemory(0, n);
                continue;
            }
            var produced = resampler.Process(block.AsSpan(0, n), output);
            if (produced > 0)
                yield return output.AsMemory(0, produced);
        }
        if (resampler is not null)
        {
            // The filter delay: the last samples of the recording.
            var tail = resampler.Flush(output);
            if (tail > 0)
                yield return output.AsMemory(0, tail);
        }
    }

    /// <summary>The model reports 0..1 for the run; the rest of the job (saving, labelling) takes the last 5 %.</summary>
    private sealed class Progress95(IJobProgressSink sink) : IProgress<double>
    {
        public void Report(double value) => sink.Progress(Math.Clamp(value, 0, 1) * 0.95);
    }

    /// <summary>App name, version and platform written into every run this app creates.</summary>
    public static AppInfo CurrentApp()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(DiarizeJobWorker).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
            version = version[..plus];
        return new AppInfo
        {
            Name = "VoiceType.Uno",
            Version = version,
            Platform = RuntimeInformation.RuntimeIdentifier.ToLower(CultureInfo.InvariantCulture),
        };
    }
}
