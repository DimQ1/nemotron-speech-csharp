using System.Security.Cryptography;
using System.Text;
using SpeechLib.Agents;
using SpeechLib.Llm;
using SpeechLib.Translation;

namespace VoiceType.Uno.History;

/// <summary>What a worker reports while it runs (the app adapts this to <c>JobExecutionContext</c>).</summary>
public interface IJobProgressSink
{
    /// <summary>A status line ("Waiting for live translation"); null clears it.</summary>
    void Detail(string? detail);

    /// <summary>Progress 0..1.</summary>
    void Progress(double fraction);

    /// <summary>A resume point; the work it describes is already on disk.</summary>
    void Checkpoint(JobCheckpoint checkpoint);
}

/// <summary>Sink that discards everything.</summary>
public sealed class NullJobProgressSink : IJobProgressSink
{
    public static readonly NullJobProgressSink Instance = new();
    public void Detail(string? detail) { }
    public void Progress(double fraction) { }
    public void Checkpoint(JobCheckpoint checkpoint) { }
}

/// <summary>Shared constants and helpers of the Gemma-backed jobs (translate, autoTitle, summarize).</summary>
public static class GemmaJobs
{
    /// <summary>Name written into translation, title and analysis metadata.</summary>
    public const string ModelName = "gemma-4-E2B-it";

    public static TranslationEngineInfo Engine() => new() { Kind = "litert-lm", Model = ModelName, Backend = "cpu" };

    /// <summary>
    /// While a job runs, turns "the host is serving a higher priority" into a status line for the Queue page
    /// and the Translate tab (<see cref="LlmHostStatus.Detail"/> when the host gives one).
    /// </summary>
    public static IDisposable WatchHost(ILlmHost host, IJobProgressSink sink)
    {
        void OnStatus(LlmHostStatus status)
        {
            var waiting = status.RunningPriority is LlmPriority.Live or LlmPriority.Interactive;
            sink.Detail(waiting
                ? status.Detail ?? (status.RunningPriority == LlmPriority.Live ? "Waiting for live translation" : "Waiting for a chat answer")
                : null);
        }

        host.StatusChanged += OnStatus;
        OnStatus(host.Status);
        return new Unsubscribe(() => host.StatusChanged -= OnStatus);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>Hash of a system prompt, stored with a translation so a changed prompt can be told from an old one.</summary>
    public static string PromptHash(string prompt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))[..12].ToLowerInvariant();

    internal static void EnsureAvailable(ILlmHost? host)
    {
        if (host is null || !host.IsModelInstalled)
            throw new LlmUnavailableException("The Gemma model is not installed. Download it on the Models page, then retry.");
    }
}

/// <summary><c>translate</c> job: a saved transcript version into the target language, in resumable units.</summary>
public sealed class TranslateJobWorker
{
    private readonly RecordingStore _store;
    private readonly ILlmHost _host;
    private readonly SavedTranslationOptions? _options;

    public TranslateJobWorker(RecordingStore store, ILlmHost host, SavedTranslationOptions? options = null)
    {
        _store = store;
        _host = host;
        _options = options;
    }

    /// <returns>Id of the translation (<c>t1</c>).</returns>
    public async Task<string?> RunAsync(HistoryJob job, IJobProgressSink sink, CancellationToken cancellationToken)
    {
        GemmaJobs.EnsureAvailable(_host);
        var entry = _store.Get(job.RecordingId) ?? throw new InvalidOperationException("The recording no longer exists.");
        var manifest = entry.Manifest;
        var versionId = job.Parameters.SourceVersion ?? manifest.CurrentVersion
            ?? throw new InvalidOperationException("The recording has no transcript to translate.");
        var version = manifest.FindVersion(versionId)
            ?? throw new InvalidOperationException($"Transcript {versionId} no longer exists.");
        var code = TranslationLanguageCodes.Normalize(job.Parameters.TargetLanguage)
            ?? throw new InvalidOperationException("No target language was chosen.");

        var transcript = _store.LoadTranscript(job.RecordingId, versionId);
        var existing = manifest.Translations.FirstOrDefault(t =>
            t.SourceVersion == versionId && string.Equals(t.TargetLanguage, code, StringComparison.OrdinalIgnoreCase));

        // A partial translation continues; a complete one (Translate again) starts over under the same id.
        TranslationDocument? resume = null;
        if (existing is { Status: not TranslationStatuses.Complete })
        {
            try
            {
                resume = _store.LoadTranslation(job.RecordingId, existing.Id);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                resume = null; // unreadable partial: start over
            }
        }

        var targetName = TranslationLanguages.NameFor(code)!;
        var sourceName = TranslationLanguages.NameFor(version.Language?.Detected ?? version.Language?.Requested);
        var info = new TranslationInfo
        {
            Id = existing?.Id ?? "",
            SourceVersion = versionId,
            TargetLanguage = code,
            Engine = GemmaJobs.Engine(),
            SystemPromptHash = GemmaJobs.PromptHash(TranslationPrompt.BuildSystemPrompt(targetName, sourceName)),
            Status = TranslationStatuses.Partial,
        };

        using var watch = GemmaJobs.WatchHost(_host, sink);
        var translator = new SavedTranscriptTranslator(_host, _options);
        string? savedId = existing?.Id;
        var finalDocument = await translator.TranslateAsync(transcript.Segments, code, sourceName ?? version.Language?.Detected, resume,
            (document, progress) =>
            {
                info.Id = savedId ?? "";
                info.SentencesDone = progress.SentencesDone;
                info.SentencesTotal = progress.SentencesTotal;
                info.Status = progress.IsComplete ? TranslationStatuses.Complete : TranslationStatuses.Partial;
                savedId = _store.SaveTranslation(job.RecordingId, info, document).Id;
                sink.Progress(progress.Fraction);
                sink.Checkpoint(new JobCheckpoint { Unit = progress.UnitsDone, ArtifactId = savedId });
            }, cancellationToken).ConfigureAwait(false);

        // Nothing to translate: still leave a (complete, empty) translation so the tab shows it.
        if (savedId is null)
        {
            info.Status = TranslationStatuses.Complete;
            savedId = _store.SaveTranslation(job.RecordingId, info, finalDocument).Id;
        }
        sink.Progress(1);
        return savedId;
    }
}

/// <summary>Language codes as stored in <see cref="TranslationInfo.TargetLanguage"/>.</summary>
public static class TranslationLanguageCodes
{
    /// <summary>"Russian" / "ru-RU" / "RU" → "ru"; unknown names → lower-case as given; auto / blank → null.</summary>
    public static string? Normalize(string? codeOrName)
    {
        var name = TranslationLanguages.NameFor(codeOrName);
        if (name is null)
            return null;
        var known = TranslationLanguages.All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        return known?.Code ?? name.ToLowerInvariant();
    }
}

/// <summary><c>autoTitle</c> job: a short title from the current transcript. Never touches the user title.</summary>
public sealed class AutoTitleJobWorker
{
    public const string ResultId = "title";

    private readonly RecordingStore _store;
    private readonly ILlmHost _host;
    private readonly TimeProvider _time;

    public AutoTitleJobWorker(RecordingStore store, ILlmHost host, TimeProvider? time = null)
    {
        _store = store;
        _host = host;
        _time = time ?? TimeProvider.System;
    }

    /// <returns><see cref="ResultId"/> when a title was stored, null when skipped.</returns>
    public async Task<string?> RunAsync(HistoryJob job, IJobProgressSink sink, CancellationToken cancellationToken)
    {
        GemmaJobs.EnsureAvailable(_host);
        var manifest = (_store.Get(job.RecordingId) ?? throw new InvalidOperationException("The recording no longer exists.")).Manifest;
        if (manifest.State == RecordingStates.Recording)
            throw new InvalidOperationException("The recording is still being recorded.");

        var regenerate = job.Parameters.Regenerate == true;
        // A title the user typed, or one Gemma already wrote, is left alone unless asked for explicitly.
        if (!regenerate && (!string.IsNullOrWhiteSpace(manifest.Title) || !string.IsNullOrWhiteSpace(manifest.AutoTitle)))
            return null;

        var versionId = job.Parameters.SourceVersion ?? manifest.CurrentVersion;
        if (versionId is null || manifest.FindVersion(versionId) is null)
            return null;
        var transcript = _store.LoadTranscript(job.RecordingId, versionId);

        using var watch = GemmaJobs.WatchHost(_host, sink);
        var title = await new AutoTitleGenerator(_host).GenerateAsync(transcript.Segments, cancellationToken).ConfigureAwait(false);
        if (title is null)
            return null;

        var info = new AutoTitleInfo
        {
            Model = GemmaJobs.ModelName,
            PromptVersion = AutoTitleGenerator.PromptVersion,
            GeneratedAt = _time.GetUtcNow(),
            SourceVersion = versionId,
        };
        // Only AutoTitle is written; manifest.Title (the user's) is never assigned here.
        _store.Update(job.RecordingId, m =>
        {
            m.AutoTitle = title;
            m.AutoTitleInfo = info;
        });
        sink.Progress(1);
        return ResultId;
    }
}

/// <summary>
/// <c>summarize</c> job: the map-reduce summary of a transcript version, cached under <c>analysis/</c>
/// (<see cref="AnalysisChunksDocument"/>, <see cref="AnalysisSummaryDocument"/>). It runs the same
/// <see cref="MapReduceSummarizer"/> over the same <see cref="AnalysisSummaryStore"/> as chat, at Background
/// priority: one chunk = one unit, saved before the next starts, so a cancelled job resumes at the next chunk and
/// chat reuses whatever the job finished (and the other way round).
/// </summary>
public sealed class SummarizeJobWorker
{
    /// <summary>Chunk size in transcript tokens (<see cref="SummaryDefaults.ChunkTokens"/>).</summary>
    public const int ChunkTokens = SummaryDefaults.ChunkTokens;

    /// <summary>Tokens of partial summaries reduced in one request.</summary>
    public const int ReduceBatchTokens = SummaryDefaults.ReduceBatchTokens;

    private readonly RecordingStore _store;
    private readonly ILlmHost _host;
    private readonly TimeProvider _time;

    public SummarizeJobWorker(RecordingStore store, ILlmHost host, TimeProvider? time = null)
    {
        _store = store;
        _host = host;
        _time = time ?? TimeProvider.System;
    }

    /// <returns>The transcript version summarized (<c>v2</c>).</returns>
    public async Task<string?> RunAsync(HistoryJob job, IJobProgressSink sink, CancellationToken cancellationToken)
    {
        GemmaJobs.EnsureAvailable(_host);
        var manifest = (_store.Get(job.RecordingId) ?? throw new InvalidOperationException("The recording no longer exists.")).Manifest;
        var versionId = job.Parameters.SourceVersion ?? manifest.CurrentVersion
            ?? throw new InvalidOperationException("The recording has no transcript.");
        if (manifest.FindVersion(versionId) is null)
            throw new InvalidOperationException($"Transcript {versionId} no longer exists.");

        using var watch = GemmaJobs.WatchHost(_host, sink);
        var summarizer = new MapReduceSummarizer(_host, new AnalysisSummaryStore(_store, _time), LlmPriority.Background);
        var target = new SummaryTarget(job.RecordingId, versionId, () => []);
        await summarizer.EnsureAsync(target, unit =>
        {
            sink.Progress((double)unit.Done / unit.Total);
            sink.Checkpoint(new JobCheckpoint { Unit = unit.Done });
        }, cancellationToken).ConfigureAwait(false);
        sink.Progress(1);
        return versionId;
    }
}

/// <summary>Cuts a transcript at segment boundaries into chunks of about a given number of tokens, with <c>[mm:ss]</c> lines (<see cref="SummaryChunker"/>).</summary>
public static class TranscriptChunker
{
    public sealed record Piece(AnalysisChunk Chunk, string Text);

    public static List<Piece> Split(IReadOnlyList<TranscriptSegment> segments, Func<string, int> countTokens, int targetTokens) =>
        SummaryChunker
            .Split(segments.Select(s => new SegmentText(s.Index, s.Start, s.End, s.Text)).ToList(), countTokens, targetTokens)
            .Select(p => new Piece(new AnalysisChunk
            {
                Index = p.Index,
                FromSegment = p.FromSegment,
                ToSegment = p.ToSegment,
                StartSec = p.StartSec,
                EndSec = p.EndSec,
                Tokens = p.Tokens,
            }, p.Text))
            .ToList();

    /// <summary><c>05:16</c> or <c>1:02:03</c>.</summary>
    public static string Clock(double seconds) => TimeText.Format(seconds);
}
