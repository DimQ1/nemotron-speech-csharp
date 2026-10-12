using SpeechLib.Agents;

namespace VoiceType.Uno.History;

/// <summary>
/// <see cref="ISummaryStore"/> over the versioned analysis documents of a recording folder
/// (<c>analysis/vN.chunks.json</c> with one summary per chunk, <c>analysis/vN.summary.json</c> for the whole
/// recording). The single cache of the map-reduce summaries: the Background <c>summarize</c> job and every chat
/// read and write the same files, keyed by version + chunk index + <see cref="AnalysisChunksDocument.TextHash"/>
/// + <see cref="AnalysisChunksDocument.PromptVersion"/> + chunk size. Each finished chunk is on disk before the
/// next one starts.
/// </summary>
public sealed class AnalysisSummaryStore : ISummaryStore
{
    /// <summary>One writer at a time across store instances (the job and chats are in one process).</summary>
    private static readonly Lock s_gate = new();

    private readonly RecordingStore _recordings;
    private readonly AnalysisStore _analysis;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);

    public AnalysisSummaryStore(RecordingStore recordings, TimeProvider? time = null)
    {
        _recordings = recordings;
        _analysis = new AnalysisStore(recordings);
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<SummaryChunk> OpenChunks(SummaryTarget target, Func<string, int> countTokens)
    {
        var segments = TryLoadSegments(target);
        if (segments is null)
            return [];
        var hash = AnalysisStore.HashText(TranscriptDocument.JoinSegments(segments));
        var pieces = TranscriptChunker.Split(segments, countTokens, SummaryDefaults.ChunkTokens);

        lock (s_gate)
        {
            _hashes[Key(target)] = hash;
            var document = _analysis.LoadChunks(target.RecordingId, target.VersionId);
            if (document is null || !document.Matches(target.VersionId, hash, SummaryDefaults.ChunkTokens) || document.Chunks.Count != pieces.Count)
            {
                var now = _time.GetUtcNow();
                document = new AnalysisChunksDocument
                {
                    Version = target.VersionId,
                    TextHash = hash,
                    ChunkTokens = SummaryDefaults.ChunkTokens,
                    Model = GemmaJobs.ModelName,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Chunks = pieces.Select(p => p.Chunk).ToList(),
                };
                _analysis.SaveChunks(target.RecordingId, document);
            }
            // The shared cache of this version exists now: the chat-only file of earlier builds is dead weight.
            _analysis.DeleteLegacyChatSummaries(target.RecordingId, target.VersionId);

            return document.Chunks
                .Select((c, i) => new SummaryChunk(c.Index, c.StartSec, c.EndSec, c.Tokens, pieces[i].Text,
                    string.IsNullOrWhiteSpace(c.Summary) ? null : c.Summary))
                .ToList();
        }
    }

    public string? GetChunkSummary(SummaryTarget target, int index)
    {
        lock (s_gate)
        {
            var document = LoadValidChunks(target);
            if (document is null || index < 0 || index >= document.Chunks.Count)
                return null;
            return string.IsNullOrWhiteSpace(document.Chunks[index].Summary) ? null : document.Chunks[index].Summary;
        }
    }

    public void SaveChunkSummary(SummaryTarget target, int index, string summary)
    {
        lock (s_gate)
        {
            var document = LoadValidChunks(target);
            if (document is null || index < 0 || index >= document.Chunks.Count)
                return; // the text changed under a running chat or job: this summary belongs to nothing now
            document.Chunks[index].Summary = summary;
            document.UpdatedAt = _time.GetUtcNow();
            document.Complete = document.Chunks.All(c => !string.IsNullOrWhiteSpace(c.Summary));
            _analysis.SaveChunks(target.RecordingId, document);
        }
    }

    public string? LoadSummary(SummaryTarget target)
    {
        lock (s_gate)
        {
            var document = LoadValidChunks(target);
            if (document is null)
                return null;
            var summary = _analysis.LoadSummary(target.RecordingId, target.VersionId);
            return summary is not null
                && summary.Matches(target.VersionId, document.TextHash)
                && summary.ChunkCount == document.Chunks.Count
                && !string.IsNullOrWhiteSpace(summary.Text)
                ? summary.Text
                : null;
        }
    }

    public void SaveSummary(SummaryTarget target, string text, int chunkCount)
    {
        lock (s_gate)
        {
            var document = LoadValidChunks(target);
            if (document is null || document.Chunks.Count != chunkCount)
                return;
            _analysis.SaveSummary(target.RecordingId, new AnalysisSummaryDocument
            {
                Version = target.VersionId,
                TextHash = document.TextHash,
                Model = GemmaJobs.ModelName,
                CreatedAt = _time.GetUtcNow(),
                ChunkCount = chunkCount,
                Text = text,
            });
        }
    }

    /// <summary>The stored chunks when they describe the transcript as it is now (hash remembered from <see cref="OpenChunks"/>).</summary>
    private AnalysisChunksDocument? LoadValidChunks(SummaryTarget target)
    {
        var document = _analysis.LoadChunks(target.RecordingId, target.VersionId);
        if (document is null)
            return null;
        if (!_hashes.TryGetValue(Key(target), out var hash))
        {
            var segments = TryLoadSegments(target);
            if (segments is null)
                return null;
            hash = _hashes[Key(target)] = AnalysisStore.HashText(TranscriptDocument.JoinSegments(segments));
        }
        return document.Matches(target.VersionId, hash, SummaryDefaults.ChunkTokens) ? document : null;
    }

    private IReadOnlyList<TranscriptSegment>? TryLoadSegments(SummaryTarget target)
    {
        try
        {
            return _recordings.LoadTranscript(target.RecordingId, target.VersionId).Segments;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string Key(SummaryTarget target) => target.RecordingId + "@" + target.VersionId;
}
