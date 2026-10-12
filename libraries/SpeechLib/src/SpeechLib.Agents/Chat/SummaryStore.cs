using System.Security.Cryptography;
using System.Text;

namespace SpeechLib.Agents;

/// <summary>
/// The one set of numbers every summary of a recording is made with, whether the Background <c>summarize</c> job
/// or a chat asks for it: the cache is shared, so chunking and prompts must be too.
/// </summary>
public static class SummaryDefaults
{
    /// <summary>
    /// Chunk size in transcript tokens. With the 4096-token window and the "background work is atomic units of
    /// about 500-800 prompt tokens" rule (research/litert/engine-improvements.md section 2): 560 tokens of text plus
    /// the ~70-token instruction is a ~650-token prefill, which cannot be cancelled and so is the longest a
    /// live-translation or chat request ever waits behind it, and it leaves room for 200 output tokens in the window.
    /// Smaller chunks (400) would cost 40 % more calls per recording for no gain.
    /// </summary>
    public const int ChunkTokens = 560;

    /// <summary>Tokens of partial summaries reduced by one request.</summary>
    public const int ReduceBatchTokens = 620;

    public const int MapMaxOutputTokens = 200;
    public const int ReduceMaxOutputTokens = 260;

    /// <summary>Bump when the prompts below change: stored summaries made with another version are rebuilt.</summary>
    public const int PromptVersion = 1;

    public const string MapPrompt =
        "You summarize one part of the transcript of a voice recording. Write 2 to 4 short sentences (at most 100 words) " +
        "with the key facts: who, what, decisions, numbers, dates, names. After each fact copy the [mm:ss] marker of " +
        "the line where it was said. Write in the same language as the transcript. Reply with the summary only.";

    public const string ReducePrompt =
        "You combine the summaries of consecutive parts of one voice recording into a single summary of 4 to 6 sentences " +
        "(at most 160 words). Keep the key facts and the [mm:ss] markers. Write in the same language as the summaries. " +
        "Reply with the summary only.";
}

/// <summary>One recording version to summarize. <paramref name="LoadLines"/> is used by stores that chunk the text themselves.</summary>
public sealed record SummaryTarget(string RecordingId, string VersionId, Func<IReadOnlyList<TranscriptLine>> LoadLines)
{
    public static SummaryTarget From(ChatSourceDocument source) => new(source.RecordingId, source.VersionId, source.LoadLines);
}

/// <summary>One map unit: <paramref name="Text"/> is the chunk as the prompt shows it (<c>[mm:ss] line</c>), <paramref name="Summary"/> is null until summarized.</summary>
public sealed record SummaryChunk(int Index, double Start, double End, int Tokens, string Text, string? Summary);

/// <summary>
/// The shared cache of the map-reduce summaries of a recording version: chunks with their summaries and the
/// final summary of the whole recording. Implemented by the app over its versioned analysis files, and in memory
/// for tests. Entries are valid only for the exact transcript text and prompt version they were made from.
/// </summary>
public interface ISummaryStore
{
    /// <summary>
    /// The chunks of the version at <see cref="SummaryDefaults.ChunkTokens"/>, with the summaries already stored.
    /// A stale cache (changed text, other prompt version or chunk size) is discarded and starts empty.
    /// </summary>
    IReadOnlyList<SummaryChunk> OpenChunks(SummaryTarget target, Func<string, int> countTokens);

    /// <summary>A fresh read of one chunk's summary (another writer may have added it since <see cref="OpenChunks"/>).</summary>
    string? GetChunkSummary(SummaryTarget target, int index);

    /// <summary>Stores one chunk summary immediately, so a cancelled run never repeats it.</summary>
    void SaveChunkSummary(SummaryTarget target, int index, string summary);

    /// <summary>The summary of the whole recording when it is valid for the current text and chunking, else null.</summary>
    string? LoadSummary(SummaryTarget target);

    void SaveSummary(SummaryTarget target, string text, int chunkCount);
}

/// <summary>Non-persistent <see cref="ISummaryStore"/> (tests, hosts without a recording store).</summary>
public sealed class InMemorySummaryStore : ISummaryStore
{
    private sealed class Entry(string hash, List<SummaryChunk> chunks)
    {
        public string Hash { get; } = hash;
        public List<SummaryChunk> Chunks { get; } = chunks;
        public string? Summary { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int ChunkSummaryCount
    {
        get { lock (_gate) return _entries.Values.Sum(e => e.Chunks.Count(c => c.Summary is not null)); }
    }

    public IReadOnlyList<SummaryChunk> OpenChunks(SummaryTarget target, Func<string, int> countTokens)
    {
        var lines = target.LoadLines();
        var hash = Hash(string.Join('\n', lines.Select(l => l.Text.Trim())));
        lock (_gate)
        {
            var key = Key(target);
            if (_entries.TryGetValue(key, out var entry) && entry.Hash == hash)
                return entry.Chunks.ToList();
            var pieces = SummaryChunker.Split(
                lines.Select((l, i) => new SegmentText(i, l.Start, l.End, l.Text)).ToList(), countTokens, SummaryDefaults.ChunkTokens);
            entry = new Entry(hash, pieces.Select(p => new SummaryChunk(p.Index, p.StartSec, p.EndSec, p.Tokens, p.Text, null)).ToList());
            _entries[key] = entry;
            return entry.Chunks.ToList();
        }
    }

    public string? GetChunkSummary(SummaryTarget target, int index)
    {
        lock (_gate)
            return _entries.TryGetValue(Key(target), out var e) && index >= 0 && index < e.Chunks.Count ? e.Chunks[index].Summary : null;
    }

    public void SaveChunkSummary(SummaryTarget target, int index, string summary)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(Key(target), out var e) && index >= 0 && index < e.Chunks.Count)
                e.Chunks[index] = e.Chunks[index] with { Summary = summary };
        }
    }

    public string? LoadSummary(SummaryTarget target)
    {
        lock (_gate)
            return _entries.TryGetValue(Key(target), out var e) ? e.Summary : null;
    }

    public void SaveSummary(SummaryTarget target, string text, int chunkCount)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(Key(target), out var e) && e.Chunks.Count == chunkCount)
                e.Summary = text;
        }
    }

    private static string Key(SummaryTarget target) => target.RecordingId + "@" + target.VersionId;

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
}

/// <summary>A transcript segment as the summary chunker sees it.</summary>
public readonly record struct SegmentText(int Index, double Start, double End, string Text);

/// <summary>One chunk of text with the segment range it covers.</summary>
public sealed record SummaryPiece(int Index, int FromSegment, int ToSegment, double StartSec, double EndSec, int Tokens, string Text);

/// <summary>
/// Cuts a transcript at segment boundaries into chunks of about a given number of tokens, one <c>[mm:ss] text</c>
/// line per segment. The only chunker of the summary pipeline (retrieval has its own, finer one).
/// </summary>
public static class SummaryChunker
{
    public static List<SummaryPiece> Split(IReadOnlyList<SegmentText> segments, Func<string, int> countTokens, int targetTokens)
    {
        var pieces = new List<SummaryPiece>();
        var lines = new StringBuilder();
        var tokensInChunk = 0;
        int from = 0, to = 0;
        double start = 0, end = 0;
        var open = false;

        void Close()
        {
            if (!open)
                return;
            pieces.Add(new SummaryPiece(pieces.Count, from, to, start, end, tokensInChunk, lines.ToString().TrimEnd()));
            open = false;
            lines.Clear();
            tokensInChunk = 0;
        }

        foreach (var segment in segments)
        {
            var text = segment.Text?.Trim() ?? "";
            if (text.Length == 0)
                continue;
            var line = $"[{TimeText.Format(segment.Start)}] {text}";
            var tokens = countTokens(line);
            // A segment longer than a whole chunk is cut by words (its tail is dropped from the prompt, not from the file).
            if (tokens > targetTokens)
            {
                var keep = (int)((long)line.Length * (targetTokens - 4) / tokens);
                var cut = line.LastIndexOf(' ', Math.Min(keep, line.Length - 1));
                line = line[..(cut > 0 ? cut : keep)] + " …";
                tokens = targetTokens;
            }
            if (open && tokensInChunk + tokens > targetTokens)
                Close();

            if (!open)
            {
                open = true;
                from = segment.Index;
                start = segment.Start;
            }
            to = segment.Index;
            end = segment.End;
            tokensInChunk += tokens;
            lines.Append(line).Append('\n');
        }
        Close();
        return pieces;
    }
}
