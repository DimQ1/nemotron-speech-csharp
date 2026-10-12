namespace VoiceType.Uno.History;

/// <summary>A file the export sheet will write.</summary>
/// <param name="FileName">Name inside the target folder.</param>
/// <param name="SourcePath">File copied as is (audio); null for generated text.</param>
/// <param name="Content">Generated text; null for copied files.</param>
/// <param name="EstimatedBytes">Size for the <c>4 files · 2.4 MB</c> summary.</param>
public sealed record HistoryExportFile(string FileName, string? SourcePath, string? Content, long EstimatedBytes);

/// <param name="SpeakerNames">Prefix every segment with its speaker (<c>Anna: </c>); ignored when the version has no speaker layer.</param>
/// <param name="Markdown">Also write a <c>.md</c> document.</param>
public sealed record HistoryExportOptions(
    bool TranscriptText, bool IncludeTranslation, bool AudioWithSubtitles, bool SpeakerNames = false, bool Markdown = false);

/// <summary>
/// Plans a recording export (decision 7): <c>.txt</c> with <c>[hh:mm:ss]</c> per segment
/// (optionally with the translation under each line), and the audio with <c>.lrc</c> and
/// <c>.srt</c> of the same base name for ordinary players.
/// </summary>
public static class HistoryUiExport
{
    public static IReadOnlyList<HistoryExportFile> Plan(
        RecordingManifest manifest,
        TranscriptDocument transcript,
        HistoryExportOptions options,
        string? audioPath,
        long audioBytes,
        TranslationDocument? translation = null,
        IReadOnlyDictionary<int, string>? speakerNames = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(options);

        var baseName = ExportFiles.SuggestBaseName(manifest);
        var usedTranslation = options.IncludeTranslation ? translation : null;
        var names = options.SpeakerNames ? speakerNames : null;
        var files = new List<HistoryExportFile>();

        if (options.TranscriptText)
        {
            var text = TranscriptExport.ToText(transcript, usedTranslation, names);
            files.Add(new HistoryExportFile(baseName + ".txt", null, text, Utf8Bytes(text)));
        }

        if (options.Markdown)
        {
            var markdown = TranscriptExport.ToMarkdown(manifest, transcript, usedTranslation, names);
            files.Add(new HistoryExportFile(baseName + ".md", null, markdown, Utf8Bytes(markdown)));
        }

        if (options.AudioWithSubtitles && !string.IsNullOrEmpty(audioPath))
        {
            var extension = Path.GetExtension(audioPath);
            files.Add(new HistoryExportFile(baseName + extension, audioPath, null, audioBytes));
            var srt = TranscriptExport.ToSrt(transcript, usedTranslation, names);
            var lrc = TranscriptExport.ToLrc(transcript, manifest.EffectiveTitle, HistoryUiFormat.DurationSeconds(manifest), names);
            files.Add(new HistoryExportFile(baseName + ".lrc", null, lrc, Utf8Bytes(lrc)));
            files.Add(new HistoryExportFile(baseName + ".srt", null, srt, Utf8Bytes(srt)));
        }

        return files;
    }

    /// <summary><c>4 files · 2.4 MB</c>; <c>Nothing selected</c> for an empty plan.</summary>
    public static string Summary(IReadOnlyList<HistoryExportFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            return "Nothing selected";
        return HistoryUiFormat.Count(files.Count, "file") + " · " + HistoryUiFormat.Bytes(files.Sum(f => f.EstimatedBytes));
    }

    /// <summary>The translation of <paramref name="versionId"/> to offer in the sheet (complete first, newest first).</summary>
    public static TranslationInfo? PickTranslation(RecordingManifest manifest, string? versionId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Translations
            .Where(t => t.SourceVersion == versionId && t.Status != TranslationStatuses.Failed)
            .OrderBy(t => t.Status == TranslationStatuses.Complete ? 0 : 1)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefault();
    }

    private static long Utf8Bytes(string text) => System.Text.Encoding.UTF8.GetByteCount(text);
}
