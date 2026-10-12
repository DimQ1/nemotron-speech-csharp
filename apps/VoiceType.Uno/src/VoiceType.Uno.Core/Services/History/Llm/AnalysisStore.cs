using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceType.Uno.History;

/// <summary>
/// Reads and writes the Gemma analysis caches of a recording folder: <c>analysis/vN.chunks.json</c> and
/// <c>analysis/vN.summary.json</c> (schemas in <see cref="AnalysisChunksDocument"/> / <see cref="AnalysisSummaryDocument"/>).
/// Files are written atomically; a file that is missing, unreadable or stale reads as null.
/// </summary>
public sealed class AnalysisStore
{
    public const string Folder = "analysis";

    private readonly RecordingStore _store;

    public AnalysisStore(RecordingStore store) => _store = store;

    public static string ChunksFile(string versionId) => $"{Folder}/{versionId}.chunks.json";

    public static string SummaryFile(string versionId) => $"{Folder}/{versionId}.summary.json";

    /// <summary>
    /// The chat-only cache of earlier builds (<c>vN.chat-summaries.json</c>), superseded by the two files above.
    /// Never read any more; deleted with its version and the first time the shared cache of the version is opened.
    /// </summary>
    public static string LegacyChatSummariesFile(string versionId) => $"{Folder}/{versionId}.chat-summaries.json";

    /// <summary>Deletes the legacy chat cache of <paramref name="versionId"/> (pure cache: Gemma rebuilds it). Never throws.</summary>
    public void DeleteLegacyChatSummaries(string recordingId, string versionId)
    {
        var folder = _store.TryGetFolder(recordingId);
        if (folder is null)
            return;
        try
        {
            File.Delete(Path.Combine(folder, LegacyChatSummariesFile(versionId)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: it is ignored anyway.
        }
    }

    /// <summary>Identity of a transcript text for cache validity.</summary>
    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();

    /// <summary>The cached chunks of <paramref name="versionId"/> (even a stale or partial file), or null.</summary>
    public AnalysisChunksDocument? LoadChunks(string recordingId, string versionId) =>
        Load(recordingId, ChunksFile(versionId), HistoryJsonContext.Default.AnalysisChunksDocument);

    public void SaveChunks(string recordingId, AnalysisChunksDocument document) =>
        Save(recordingId, ChunksFile(document.Version), document, HistoryJsonContext.Default.AnalysisChunksDocument);

    public AnalysisSummaryDocument? LoadSummary(string recordingId, string versionId) =>
        Load(recordingId, SummaryFile(versionId), HistoryJsonContext.Default.AnalysisSummaryDocument);

    public void SaveSummary(string recordingId, AnalysisSummaryDocument document) =>
        Save(recordingId, SummaryFile(document.Version), document, HistoryJsonContext.Default.AnalysisSummaryDocument);

    private T? Load<T>(string recordingId, string relativePath, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        var folder = _store.TryGetFolder(recordingId);
        if (folder is null)
            return null;
        var path = Path.Combine(folder, relativePath);
        if (!File.Exists(path))
            return null;
        try
        {
            return HistoryJson.Read(path, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save<T>(string recordingId, string relativePath, T document, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var folder = _store.TryGetFolder(recordingId)
            ?? throw new KeyNotFoundException($"Recording {recordingId} does not exist.");
        HistoryJson.WriteAtomic(Path.Combine(folder, relativePath), document, typeInfo);
    }
}
