using System.Globalization;
using SpeechLib.Translation;

namespace VoiceType.Uno.History;

/// <summary>Enqueue rules of the Gemma jobs (translate, autoTitle, summarize).</summary>
public static class HistoryLlmJobs
{
    /// <summary>
    /// Queues a translation of <paramref name="sourceVersion"/> (null = the current version, pinned now) into
    /// <paramref name="targetLanguage"/>. When the same translation is already queued, running or paused, that job is returned
    /// instead of a second one. A partial translation of the same version and language is continued by the job.
    /// </summary>
    public static HistoryJob EnqueueTranslate(JobQueue queue, RecordingManifest manifest, string? sourceVersion, string targetLanguage)
    {
        var version = sourceVersion ?? manifest.CurrentVersion
            ?? throw new InvalidOperationException("The recording has no transcript to translate.");
        var code = TranslationLanguageCodes.Normalize(targetLanguage)
            ?? throw new ArgumentException("A translation needs a target language.", nameof(targetLanguage));

        var existing = FindUnfinished(queue.Jobs, manifest.Id, HistoryJobKind.Translate, j =>
            j.Parameters.SourceVersion == version
            && string.Equals(TranslationLanguageCodes.Normalize(j.Parameters.TargetLanguage), code, StringComparison.Ordinal));
        return existing ?? queue.Enqueue(HistoryJobKind.Translate, manifest.Id,
            new JobParameters { SourceVersion = version, TargetLanguage = code },
            priority: JobPriorities.Normal);
    }

    /// <summary>Queues a title job (low priority). Returns null when one is already waiting for this recording.</summary>
    public static HistoryJob? EnqueueAutoTitle(JobQueue queue, string recordingId, bool regenerate = false)
    {
        if (FindUnfinished(queue.Jobs, recordingId, HistoryJobKind.AutoTitle, _ => true) is not null)
            return null;
        return queue.Enqueue(HistoryJobKind.AutoTitle, recordingId,
            regenerate ? new JobParameters { Regenerate = true } : null,
            priority: regenerate ? JobPriorities.Normal : JobPriorities.Low);
    }

    /// <summary>
    /// Queues the map-reduce summary of a version (chat preparation). Returns the waiting job when there is one; a
    /// waiting job of lower priority than <paramref name="priority"/> that has not started is replaced by one at the
    /// wanted priority (its finished chunks are in the shared cache, so nothing is lost).
    /// </summary>
    public static HistoryJob EnqueueSummarize(JobQueue queue, string recordingId, string? sourceVersion = null, int priority = JobPriorities.Low)
    {
        var existing = FindUnfinished(queue.Jobs, recordingId, HistoryJobKind.Summarize, j => j.Parameters.SourceVersion == sourceVersion);
        if (existing is not null && existing.Priority < priority && existing.State is JobState.Queued or JobState.Waiting)
        {
            queue.Cancel(existing.Id);
            existing = null;
        }
        return existing ?? queue.Enqueue(HistoryJobKind.Summarize, recordingId,
            new JobParameters { SourceVersion = sourceVersion }, priority: priority);
    }

    /// <summary>Recordings longer than this get their summaries prepared in the background (shorter ones fit a chat window whole).</summary>
    public const double PrepareChatMinSeconds = 300;

    /// <summary>
    /// After a recording was saved: queues a Background <c>summarize</c> job when the recording is longer than
    /// <see cref="PrepareChatMinSeconds"/>, the Gemma model is installed and the History › Titles by Gemma switch
    /// (<see cref="HistorySettings.AutoTitles"/>, the one "let Gemma prepare my recordings" toggle) is on. Duplicates are
    /// merged by <see cref="EnqueueSummarize"/>. Never throws.
    /// </summary>
    public static void TryEnqueueSummarizeAfterSave(JobQueue queue, RecordingStore recordings, string recordingId, HistorySettings settings, bool modelInstalled)
    {
        if (!ShouldAutoTitle(settings, modelInstalled) || queue.IsReadOnly)
            return;
        try
        {
            var manifest = recordings.Get(recordingId)?.Manifest;
            if (manifest?.CurrentVersion is { } version && manifest.Audio?.DurationSec > PrepareChatMinSeconds)
                EnqueueSummarize(queue, recordingId, version, JobPriorities.Low);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.WriteLine($"[VoiceType.Uno] Summary job was not queued: {ex.Message}");
        }
    }

    /// <summary>
    /// "Preparing the recording… 3/12" for a recording whose summarize job is queued or running, else null.
    /// The numbers come from the shared chunks document (finished chunks of the total + the reduce step).
    /// </summary>
    public static string? PreparationText(JobQueue queue, RecordingStore recordings, string recordingId, string versionId)
    {
        var job = FindUnfinished(queue.Jobs, recordingId, HistoryJobKind.Summarize, j => j.Parameters.SourceVersion is null || j.Parameters.SourceVersion == versionId);
        if (job is null)
            return null;
        var chunks = new AnalysisStore(recordings).LoadChunks(recordingId, versionId);
        if (chunks is null || chunks.Chunks.Count == 0)
            return "Preparing the recording…";
        var done = chunks.Chunks.Count(c => !string.IsNullOrWhiteSpace(c.Summary));
        return string.Create(CultureInfo.InvariantCulture, $"Preparing the recording… {done}/{chunks.Chunks.Count + 1}");
    }

    /// <summary>Title jobs run after a save only when the setting is on and the model is installed.</summary>
    public static bool ShouldAutoTitle(HistorySettings settings, bool modelInstalled) =>
        settings.AutoTitles && modelInstalled;

    /// <summary>
    /// Queues a title job after a recording was saved, when <see cref="ShouldAutoTitle"/> allows it.
    /// Never throws: a title is a nicety.
    /// </summary>
    public static void TryEnqueueAutoTitleAfterSave(JobQueue queue, string recordingId, HistorySettings settings, bool modelInstalled)
    {
        if (!ShouldAutoTitle(settings, modelInstalled) || queue.IsReadOnly)
            return;
        try
        {
            EnqueueAutoTitle(queue, recordingId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.WriteLine($"[VoiceType.Uno] Title job was not queued: {ex.Message}");
        }
    }

    public static HistoryJob? FindUnfinished(IEnumerable<HistoryJob> jobs, string recordingId, HistoryJobKind kind, Func<HistoryJob, bool> match) =>
        jobs.FirstOrDefault(j => !j.IsFinished && j.Kind == kind && j.RecordingId == recordingId && match(j));
}

/// <summary>One saved translation as the Translate tab lists it.</summary>
/// <param name="StatusText"><c>Complete</c> or <c>Partial · 6 / 14 sentences</c>.</param>
public sealed record TranslationListItem(
    string Id, string TargetLanguage, string LanguageName, string SourceVersion, bool IsOutdated, bool IsPartial,
    string StatusText, string Label);

/// <summary>One line of the bilingual transcript.</summary>
/// <param name="Translation">Null while the segment is not translated yet; "" when it only continues a sentence that started earlier.</param>
public sealed record BilingualRow(int Position, int SegmentIndex, double Start, double End, string TimeText, string Original, string? Translation)
{
    public bool IsTranslated => Translation is not null;
    public bool HasTranslationText => !string.IsNullOrEmpty(Translation);
}

/// <summary>Logic of the Translate tab that needs no UI.</summary>
public static class TranslateTabLogic
{
    /// <summary>
    /// A translation is outdated when the recording's current version is no longer the one it was made from
    /// (the text was edited or re-recognized). It stays readable, and "Translate again" refreshes it.
    /// </summary>
    public static bool IsOutdated(RecordingManifest manifest, TranslationInfo translation) =>
        manifest.CurrentVersion is { } current && !string.Equals(translation.SourceVersion, current, StringComparison.Ordinal);

    public static string LanguageName(string code) => TranslationLanguages.NameFor(code) ?? code;

    /// <summary>Translations of the recording, newest first.</summary>
    public static List<TranslationListItem> List(RecordingManifest manifest) =>
        manifest.Translations
            .Where(t => t.Status != TranslationStatuses.Failed || t.SentencesDone > 0)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t =>
            {
                var partial = t.Status != TranslationStatuses.Complete;
                var outdated = IsOutdated(manifest, t);
                var name = LanguageName(t.TargetLanguage);
                var status = partial
                    ? $"Partial · {t.SentencesDone.ToString(CultureInfo.InvariantCulture)} / {t.SentencesTotal.ToString(CultureInfo.InvariantCulture)} sentences"
                    : "Complete";
                return new TranslationListItem(t.Id, t.TargetLanguage, name, t.SourceVersion, outdated, partial, status,
                    $"→ {name} · from {t.SourceVersion}" + (outdated ? " · outdated" : ""));
            })
            .ToList();

    /// <summary>The unfinished translate job for this recording (queued, waiting, running or paused), if any.</summary>
    public static HistoryJob? ActiveJob(IEnumerable<HistoryJob> jobs, string recordingId) =>
        HistoryLlmJobs.FindUnfinished(jobs, recordingId, HistoryJobKind.Translate, _ => true);

    /// <summary>
    /// Original and translation per segment of the viewed version. <paramref name="translation"/> must be made from
    /// the same version (indexes match); rows past the translated part have a null translation.
    /// </summary>
    public static List<BilingualRow> BuildRows(IReadOnlyList<TranscriptSegment> segments, TranslationDocument? translation)
    {
        var rows = new List<BilingualRow>(segments.Count);
        var byIndex = translation?.Segments.ToDictionary(s => s.Index, s => s.Text) ?? [];
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            rows.Add(new BilingualRow(i, segment.Index, segment.Start, segment.End, HistoryUiFormat.SegmentTime(segment.Start),
                segment.Text.Trim(), byIndex.TryGetValue(segment.Index, out var text) ? text.Trim() : null));
        }
        return rows;
    }

    /// <summary><c>Translating to German · 6 / 14 sentences</c>.</summary>
    public static string ProgressText(string languageName, int done, int total) =>
        total <= 0
            ? $"Translating to {languageName}…"
            : $"Translating to {languageName} · {done.ToString(CultureInfo.InvariantCulture)} / {total.ToString(CultureInfo.InvariantCulture)} sentences";
}
