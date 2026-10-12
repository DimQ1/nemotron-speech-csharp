using System.Security.Cryptography;
using System.Text;
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
/// <c>summarize</c> job: map-reduce summary pieces of a transcript version, cached under <c>analysis/</c> for chats
/// (<see cref="AnalysisChunksDocument"/>, <see cref="AnalysisSummaryDocument"/>). One chunk = one unit.
/// </summary>
public sealed class SummarizeJobWorker
{
    /// <summary>Chunk size in transcript tokens: with the instructions a map request is ≈ 700 prompt tokens.</summary>
    public const int ChunkTokens = 560;

    /// <summary>Tokens of partial summaries reduced in one request.</summary>
    public const int ReduceBatchTokens = 620;

    private const string MapPrompt =
        "You summarize one part of the transcript of a voice recording. Write 2 to 4 short sentences (at most 100 words) " +
        "with the key facts: who, what, decisions, numbers, dates, names. After each fact copy the [mm:ss] marker of " +
        "the line where it was said. Write in the same language as the transcript. Reply with the summary only.";

    private const string ReducePrompt =
        "You combine the summaries of consecutive parts of one voice recording into a single summary of 4 to 6 sentences " +
        "(at most 160 words). Keep the key facts and the [mm:ss] markers. Write in the same language as the summaries. " +
        "Reply with the summary only.";

    private readonly RecordingStore _store;
    private readonly AnalysisStore _analysis;
    private readonly ILlmHost _host;
    private readonly TimeProvider _time;

    public SummarizeJobWorker(RecordingStore store, ILlmHost host, TimeProvider? time = null)
    {
        _store = store;
        _analysis = new AnalysisStore(store);
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

        var transcript = _store.LoadTranscript(job.RecordingId, versionId);
        var hash = AnalysisStore.HashText(TranscriptDocument.JoinSegments(transcript.Segments));
        var chunkTexts = TranscriptChunker.Split(transcript.Segments, _host.CountTokens, ChunkTokens);

        var document = _analysis.LoadChunks(job.RecordingId, versionId);
        if (document is null || !document.Matches(versionId, hash, ChunkTokens) || document.Chunks.Count != chunkTexts.Count)
        {
            var now = _time.GetUtcNow();
            document = new AnalysisChunksDocument
            {
                Version = versionId,
                TextHash = hash,
                ChunkTokens = ChunkTokens,
                Model = GemmaJobs.ModelName,
                CreatedAt = now,
                UpdatedAt = now,
                Chunks = chunkTexts.Select(c => c.Chunk).ToList(),
            };
            _analysis.SaveChunks(job.RecordingId, document);
        }

        using var watch = GemmaJobs.WatchHost(_host, sink);
        var total = document.Chunks.Count + 1; // + the reduce step
        for (var i = 0; i < document.Chunks.Count; i++)
        {
            var chunk = document.Chunks[i];
            if (!string.IsNullOrWhiteSpace(chunk.Summary))
                continue;
            cancellationToken.ThrowIfCancellationRequested();

            chunk.Summary = await GenerateAsync(MapPrompt, chunkTexts[i].Text, 200, cancellationToken).ConfigureAwait(false);
            document.UpdatedAt = _time.GetUtcNow();
            document.Complete = document.Chunks.All(c => !string.IsNullOrWhiteSpace(c.Summary));
            _analysis.SaveChunks(job.RecordingId, document);
            sink.Progress((double)(i + 1) / total);
            sink.Checkpoint(new JobCheckpoint { Unit = i + 1 });
        }

        var summary = _analysis.LoadSummary(job.RecordingId, versionId);
        if (summary is null || !summary.Matches(versionId, hash) || summary.ChunkCount != document.Chunks.Count)
        {
            var text = await ReduceAsync(document.Chunks.Select(c => c.Summary!).ToList(), cancellationToken).ConfigureAwait(false);
            _analysis.SaveSummary(job.RecordingId, new AnalysisSummaryDocument
            {
                Version = versionId,
                TextHash = hash,
                Model = GemmaJobs.ModelName,
                CreatedAt = _time.GetUtcNow(),
                ChunkCount = document.Chunks.Count,
                Text = text,
            });
        }
        sink.Progress(1);
        return versionId;
    }

    /// <summary>Reduces partial summaries in batches of <see cref="ReduceBatchTokens"/> until one is left.</summary>
    private async Task<string> ReduceAsync(List<string> pieces, CancellationToken cancellationToken)
    {
        if (pieces.Count == 0)
            return "";
        while (pieces.Count > 1)
        {
            var next = new List<string>();
            var batch = new List<string>();
            var tokens = 0;
            async Task FlushAsync()
            {
                if (batch.Count == 0)
                    return;
                next.Add(batch.Count == 1
                    ? batch[0]
                    : await GenerateAsync(ReducePrompt, string.Join("\n", batch), 260, cancellationToken).ConfigureAwait(false));
                batch.Clear();
                tokens = 0;
            }

            foreach (var piece in pieces)
            {
                var cost = _host.CountTokens(piece);
                if (batch.Count > 0 && tokens + cost > ReduceBatchTokens)
                    await FlushAsync().ConfigureAwait(false);
                batch.Add(piece);
                tokens += cost;
            }
            await FlushAsync().ConfigureAwait(false);
            if (next.Count >= pieces.Count)
                break; // nothing merged (each piece alone fills a batch): stop instead of looping
            pieces = next;
        }
        return string.Join(" ", pieces);
    }

    private async Task<string> GenerateAsync(string system, string user, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var request = new LlmRequest(LlmPriority.Background,
            [new LlmMessage(LlmRole.System, system), new LlmMessage(LlmRole.User, user)],
            new LlmSampling(Temperature: 0.2, TopP: 0.5, MaxOutputTokens: maxOutputTokens));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var text = TranslationOutputCleaner.Clean(await _host.GenerateAsync(request, cancellationToken).ConfigureAwait(false));
                if (text.Length == 0)
                    throw new InvalidOperationException("Gemma returned an empty summary.");
                return text;
            }
            catch (LlmUnavailableException)
            {
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Cuts a transcript at segment boundaries into chunks of about a given number of tokens, with <c>[mm:ss]</c> lines.</summary>
public static class TranscriptChunker
{
    public sealed record Piece(AnalysisChunk Chunk, string Text);

    public static List<Piece> Split(IReadOnlyList<TranscriptSegment> segments, Func<string, int> countTokens, int targetTokens)
    {
        var pieces = new List<Piece>();
        var lines = new StringBuilder();
        AnalysisChunk? chunk = null;

        void Close()
        {
            if (chunk is null)
                return;
            pieces.Add(new Piece(chunk, lines.ToString().TrimEnd()));
            chunk = null;
            lines.Clear();
        }

        foreach (var segment in segments)
        {
            var text = segment.Text?.Trim() ?? "";
            if (text.Length == 0)
                continue;
            var line = $"[{Clock(segment.Start)}] {text}";
            var tokens = countTokens(line);
            // A segment longer than a whole chunk is cut by words (its tail is dropped from the prompt, not from the file).
            if (tokens > targetTokens)
            {
                var keep = (int)((long)line.Length * (targetTokens - 4) / tokens);
                var cut = line.LastIndexOf(' ', Math.Min(keep, line.Length - 1));
                line = line[..(cut > 0 ? cut : keep)] + " …";
                tokens = targetTokens;
            }
            if (chunk is not null && chunk.Tokens + tokens > targetTokens)
                Close();

            chunk ??= new AnalysisChunk
            {
                Index = pieces.Count,
                FromSegment = segment.Index,
                StartSec = segment.Start,
            };
            chunk.ToSegment = segment.Index;
            chunk.EndSec = segment.End;
            chunk.Tokens += tokens;
            lines.Append(line).Append('\n');
        }
        Close();
        return pieces;
    }

    /// <summary><c>05:16</c> or <c>1:02:03</c>.</summary>
    public static string Clock(double seconds)
    {
        var total = (long)Math.Max(0, seconds);
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }
}
