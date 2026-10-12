using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SpeechLib;
using SpeechLib.AudioStorage;
using SpeechLib.Providers;
using VoiceType.Uno.History;
using AppInfo = VoiceType.Uno.History.AppInfo;

namespace VoiceType.Uno.Services.Jobs;

/// <summary>
/// <c>rerecognize</c> jobs (design §9.1–9.2): decodes the stored audio (Ogg Opus / WAV) to
/// 16 kHz mono, runs the chosen installed ASR model over it as fast as it decodes with its own
/// recognizer instance (never the one of the main screen; disposed when the job ends), builds
/// segments with timings (<see cref="OfflineTranscriber"/>) and saves the result as a new
/// <see cref="VersionKinds.Rerecognized"/> version with the full model, language, settings,
/// app and speed metadata. Resumes from the checkpoint of an interrupted run.
/// </summary>
public sealed class OfflineRecognitionJob : IJobExecutor
{
    /// <summary><see cref="JobParameters.Language"/>: take the language of the source version.</summary>
    public const string KeepLanguage = "keep";
    public const string AutoLanguage = "auto";

    /// <summary>Keys of <see cref="JobParameters.Settings"/> (and of the version's <c>settings</c>).</summary>
    public static class SettingKeys
    {
        public const string UseVad = "useVad";
        public const string RepetitionPenalty = "repetitionPenalty";
        public const string ExecutionProvider = "executionProvider";
        public const string ChunkSec = "chunkSec";
        public const string RightContextSec = "rightContextSec";
        public const string LeftContextSec = "leftContextSec";
        public const string SilenceContextSec = "silenceContextSec";
        public const string StopHistoryEouSec = "stopHistoryEouSec";
        public const string Pipeline = "pipeline";
        public const string AudioCodec = "audioCodec";
        public const string AudioBitrate = "audioBitrate";
        public const string SourceVersion = "sourceVersion";
        public const string ResumedAtSec = "resumedAtSec";
    }

    /// <summary>
    /// Parakeet step for re-recognition when the job does not set <c>chunkSec</c>. Latency does
    /// not matter offline: 1.0 s steps measured 13.8% WER at RTF 0.19 against 14.2% at RTF 0.44
    /// with the live 0.32 s step (WerEval --streaming, 100 CV17 en files through Opus 48 kbps).
    /// Larger steps lose accuracy (2.0 s: +7 pp on a 99 s recording).
    /// </summary>
    public const double OfflineParakeetChunkSeconds = 1.0;

    private readonly RecordingStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppSettings, IReadOnlyList<string>> _modelRoots;
    private readonly Func<RecognizerFactoryOptions, IStreamingSpeechRecognizer> _createRecognizer;

    public OfflineRecognitionJob(
        RecordingStore store,
        Func<AppSettings> settings,
        Func<AppSettings, IReadOnlyList<string>>? modelRoots = null,
        Func<RecognizerFactoryOptions, IStreamingSpeechRecognizer>? createRecognizer = null)
    {
        _store = store;
        _settings = settings;
        _modelRoots = modelRoots ?? InstalledAsrModels.DefaultRoots;
        _createRecognizer = createRecognizer ?? RecognizerFactory.Create;
    }

    public HistoryJobKind Kind => HistoryJobKind.Rerecognize;

    public Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken)
    {
        var entry = _store.Get(job.RecordingId)
            ?? throw new InvalidOperationException("The recording no longer exists.");
        var manifest = entry.Manifest;
        if (manifest.State == RecordingStates.Recording)
            throw new InvalidOperationException("The recording is still being recorded.");
        var audioPath = _store.GetAudioPath(job.RecordingId)
            ?? throw new InvalidOperationException("The audio of this recording was deleted; only the text is left.");

        var app = _settings();
        var parameters = job.Parameters;
        var folder = !string.IsNullOrWhiteSpace(parameters.ModelFolder)
            ? parameters.ModelFolder
            : Path.GetFileName(app.ModelPath.TrimEnd('/', '\\'));
        var model = InstalledAsrModels.Find(folder, _modelRoots(app))
            ?? throw new InvalidOperationException($"The model '{ModelTitle(folder)}' is not installed.");

        var sourceVersion = parameters.SourceVersion is { } requestedSource && manifest.FindVersion(requestedSource) is { } found
            ? found
            : manifest.Current;
        var language = ResolveLanguage(parameters.Language, sourceVersion);
        var options = BuildOptions(model, language, parameters.Settings, app);

        cancellationToken.ThrowIfCancellationRequested();
        context.ReportDetail("Loading " + model.Title + "…");
        using var recognizer = _createRecognizer(options);
        cancellationToken.ThrowIfCancellationRequested();

        context.ReportDetail("Reading the audio…");
        var samples = RecordingAudioReader.ReadMono(audioPath, recognizer.SampleRate);
        double rate = recognizer.SampleRate;
        cancellationToken.ThrowIfCancellationRequested();

        var partPath = OfflinePartFile.PathFor(context.JobsDir, job.Id);
        var resume = job.Checkpoint is null ? null : OfflinePartFile.TryLoad(job.Checkpoint.PartFile ?? partPath);
        if (resume is not null && resume.Sample > samples.LongLength)
            resume = null;

        context.ReportDetail(null);
        var watch = Stopwatch.StartNew();
        var result = OfflineTranscriber.Run(recognizer, samples, new OfflineTranscriptionOptions
        {
            StartSample = resume?.Sample ?? 0,
            CommittedSegments = resume?.Segments,
            Progress = p => context.ReportProgress(p.Fraction, p.Processed, p.Total),
            Checkpoint = checkpoint => context.OfferCheckpoint(() =>
            {
                OfflinePartFile.Save(partPath, checkpoint);
                return new JobCheckpoint { Sample = checkpoint.Sample, PartFile = partPath };
            }),
        }, cancellationToken);
        watch.Stop();

        cancellationToken.ThrowIfCancellationRequested();
        double decodedSeconds = (samples.LongLength - (resume?.Sample ?? 0)) / rate;
        var settings = DescribeSettings(options, recognizer, manifest, sourceVersion);
        if (resume is not null)
            settings[SettingKeys.ResumedAtSec] = JsonValues.From(Math.Round(resume.Sample / rate, 2));

        var spec = new TranscriptVersion
        {
            Kind = VersionKinds.Rerecognized,
            Model = model.ToModelInfo(),
            Language = new LanguageInfo
            {
                Requested = language,
                Detected = result.DetectedLanguage ?? (language == AutoLanguage ? null : language),
            },
            Settings = settings,
            App = CurrentApp(),
            Stats = new VersionStats
            {
                Rtf = decodedSeconds > 0 ? Math.Round(watch.Elapsed.TotalSeconds / decodedSeconds, 3) : null,
                ElapsedSec = Math.Round(watch.Elapsed.TotalSeconds, 1),
            },
        };

        var version = _store.AddVersion(job.RecordingId, spec, result.Document, makeCurrent: parameters.MakeCurrent == true);
        return Task.FromResult<string?>(version.Id);
    }

    /// <summary>
    /// <c>keep</c> (or null) → the source version's detected language, else its requested one,
    /// else auto; any other value is used as given.
    /// </summary>
    public static string ResolveLanguage(string? requested, TranscriptVersion? source)
    {
        if (!string.IsNullOrWhiteSpace(requested) && !string.Equals(requested, KeepLanguage, StringComparison.OrdinalIgnoreCase))
            return requested.Trim();
        var detected = source?.Language?.Detected;
        if (!string.IsNullOrWhiteSpace(detected))
            return detected;
        var previous = source?.Language?.Requested;
        return string.IsNullOrWhiteSpace(previous) ? AutoLanguage : previous;
    }

    private static RecognizerFactoryOptions BuildOptions(
        InstalledAsrModel model, string language, Dictionary<string, JsonElement>? settings, AppSettings app) => new()
    {
        ModelPath = model.Path,
        ExecutionProvider = GetString(settings, SettingKeys.ExecutionProvider) ?? app.ExecutionProvider,
        Language = language,
        UseVad = GetBool(settings, SettingKeys.UseVad) ?? app.UseVad,
        RepetitionPenalty = GetDouble(settings, SettingKeys.RepetitionPenalty) ?? app.RepetitionPenalty,
        StreamingChunkSeconds = GetDouble(settings, SettingKeys.ChunkSec)
            ?? (model.Family == AsrModelFamilies.ParakeetTdt ? OfflineParakeetChunkSeconds : null),
        StreamingRightContextSeconds = GetDouble(settings, SettingKeys.RightContextSec),
        StreamingLeftContextSeconds = GetDouble(settings, SettingKeys.LeftContextSec),
        StreamingSilenceContextSeconds = GetDouble(settings, SettingKeys.SilenceContextSec),
    };

    /// <summary>Everything that affects the result (design §9.2), so WER differences can be explained.</summary>
    private static Dictionary<string, JsonElement> DescribeSettings(
        RecognizerFactoryOptions options, IStreamingSpeechRecognizer recognizer, RecordingManifest manifest, TranscriptVersion? source)
    {
        var settings = JsonValues.Settings(
            (SettingKeys.Pipeline, "offline-streaming"),
            (SettingKeys.UseVad, options.UseVad),
            (SettingKeys.RepetitionPenalty, options.RepetitionPenalty),
            (SettingKeys.ExecutionProvider, options.ExecutionProvider));

        if (recognizer is SpeechLib.ParakeetTdt.ParakeetTdtRecognizer parakeet)
        {
            settings[SettingKeys.ChunkSec] = JsonValues.From(Math.Round(parakeet.ChunkSeconds, 3));
            settings[SettingKeys.RightContextSec] = JsonValues.From(Math.Round(parakeet.RightContextSeconds, 3));
            settings[SettingKeys.LeftContextSec] = JsonValues.From(Math.Round(parakeet.LeftContextSeconds, 3));
            settings[SettingKeys.SilenceContextSec] = JsonValues.From(Math.Round(parakeet.SilenceContextSeconds, 3));
        }
        else
        {
            settings[SettingKeys.ChunkSec] = JsonValues.From(Math.Round(recognizer.ChunkSamples / (double)recognizer.SampleRate, 3));
        }
        if (recognizer is IUtteranceStreamingRecognizer utterance)
            settings[SettingKeys.StopHistoryEouSec] = JsonValues.From(utterance.StopHistoryEouSeconds);

        if (manifest.Audio is { } audio)
        {
            if (!string.IsNullOrEmpty(audio.Codec))
                settings[SettingKeys.AudioCodec] = JsonValues.From(audio.Codec);
            if (audio.Bitrate is { } bitrate)
                settings[SettingKeys.AudioBitrate] = JsonValues.From(bitrate);
        }
        if (source is not null)
            settings[SettingKeys.SourceVersion] = JsonValues.From(source.Id);
        return settings;
    }

    private static string ModelTitle(string folder) => SpeechLib.ModelDownload.ModelCatalog.DescribeFolder(folder);

    private static string? GetString(Dictionary<string, JsonElement>? settings, string key) =>
        settings?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(Dictionary<string, JsonElement>? settings, string key) =>
        settings?.TryGetValue(key, out var value) == true && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? GetDouble(Dictionary<string, JsonElement>? settings, string key) =>
        settings?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>App name, version and platform written into every version this app creates.</summary>
    public static AppInfo CurrentApp()
    {
        var assembly = typeof(OfflineRecognitionJob).Assembly;
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
