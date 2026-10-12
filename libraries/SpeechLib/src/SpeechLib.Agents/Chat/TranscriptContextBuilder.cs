using System.Text;

namespace SpeechLib.Agents;

public enum ContextMode
{
    /// <summary>No active source.</summary>
    None,

    /// <summary>Every transcript fits the budget: all of it is included.</summary>
    Full,

    /// <summary>Best BM25 chunks (and cached digests when available).</summary>
    Excerpts,

    /// <summary>Map-reduce digest of every source (summary-style questions over long recordings).</summary>
    Digest,

    /// <summary>Best-matching single lines found by cue words (action items, decisions…).</summary>
    Lines,
}

/// <summary>The transcript context of one turn.</summary>
/// <param name="Text">What goes in front of the question.</param>
/// <param name="Tokens">Measured size of <paramref name="Text"/>.</param>
/// <param name="Used">(alias, first line time) of every included piece, for diagnostics.</param>
public sealed record BuiltContext(string Text, ContextMode Mode, int Tokens, IReadOnlyList<(string Alias, double Start)> Used);

/// <summary>
/// Chooses what of the chat's recordings the model sees for one question, within a token budget.
/// Pure logic over <see cref="ChatSourceDocument"/>: no app stores, no Agent Framework.
/// </summary>
public sealed class TranscriptContextBuilder
{
    private const int MinUsefulTokens = 70;
    private const double RelativeScoreCutoff = 0.4;
    private const int MaxRepeatedLines = 3;

    private readonly Func<string, int> _count;
    private readonly MapReduceSummarizer? _summarizer;
    private readonly Dictionary<string, IReadOnlyList<TranscriptChunk>> _chunkCache = new(StringComparer.Ordinal);
    private string? _indexKey;
    private Bm25Index? _chunkIndex;
    private List<(ChatSourceDocument Source, TranscriptChunk Chunk)>? _indexedChunks;
    private Bm25Index? _lineIndex;
    private List<(ChatSourceDocument Source, TranscriptLine Line)>? _indexedLines;

    public TranscriptContextBuilder(Func<string, int> countTokens, MapReduceSummarizer? summarizer)
    {
        _count = countTokens ?? throw new ArgumentNullException(nameof(countTokens));
        _summarizer = summarizer;
    }

    public async Task<BuiltContext> BuildAsync(
        IReadOnlyList<ChatSourceDocument> sources,
        string question,
        string? previousQuestion,
        int budgetTokens,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var active = sources.Where(s => s.Active).ToList();
        if (active.Count == 0)
            return new BuiltContext("No recordings are available.\n\nQuestion:", ContextMode.None, 0, []);

        var perSource = active.Select(s => (Source: s, Chunks: ChunksOf(s))).ToList();
        EnsureIndex(perSource);

        var total = perSource.Sum(p => p.Chunks.Sum(c => c.Tokens));
        var kind = ChatPrompts.Classify(question);
        var used = new List<(string, double)>();

        if (total <= budgetTokens)
        {
            var text = new StringBuilder("Transcript").Append(active.Count > 1 ? "s" : "").Append(":\n");
            foreach (var (source, chunks) in perSource)
            {
                foreach (var chunk in chunks)
                {
                    AppendChunk(text, source, chunk);
                    used.Add((source.Alias, chunk.Start));
                }
            }
            return Finish(text, ContextMode.Full, used);
        }

        if (kind == QuestionKind.Overview && _summarizer is not null)
        {
            var share = Math.Max(60, (int)(budgetTokens * 0.9) / active.Count);
            var text = new StringBuilder();
            foreach (var (source, _) in perSource)
            {
                var digest = await _summarizer.GetDigestAsync(source, share, allowGenerate: true, progress, cancellationToken).ConfigureAwait(false);
                if (digest is null || digest.Items.Count == 0)
                    continue;
                text.Append("Summary of ").Append(source.Alias)
                    .Append(" (the whole recording; each line covers the part that starts at its time):\n")
                    .Append(digest.Render(source.Alias)).Append("\n\n");
                used.AddRange(digest.Items.Select(i => (source.Alias, i.Start)));
            }
            return Finish(text, ContextMode.Digest, used);
        }

        var remaining = budgetTokens;
        var body = new StringBuilder();

        // Cached digests are free: give them a quarter of the budget when every source has one.
        if (_summarizer is not null)
        {
            var digestShare = Math.Max(60, budgetTokens / 4 / active.Count);
            var digests = new List<(ChatSourceDocument Source, Digest Digest)>();
            foreach (var (source, _) in perSource)
            {
                var digest = await _summarizer.GetDigestAsync(source, digestShare, allowGenerate: false, null, cancellationToken).ConfigureAwait(false);
                if (digest is null)
                {
                    digests.Clear();
                    break;
                }
                digests.Add((source, digest));
            }
            foreach (var (source, digest) in digests)
            {
                if (digest.Items.Count == 0)
                    continue;
                body.Append("Summary of ").Append(source.Alias).Append(" (whole recording):\n").Append(digest.Render(source.Alias)).Append("\n\n");
                remaining -= digest.Tokens + 8;
                used.AddRange(digest.Items.Select(i => (source.Alias, i.Start)));
            }
        }

        var query = BuildQuery(question, previousQuestion, kind);
        if (kind == QuestionKind.Extraction)
        {
            var selected = SelectLines(query, remaining, active.Count);
            body.Append("Matching transcript lines (not the whole recording):\n");
            foreach (var (source, line) in selected)
            {
                body.Append(TranscriptChunker.RenderLine(source.Alias, line)).Append('\n');
                used.Add((source.Alias, line.Start));
            }
            return Finish(body, ContextMode.Lines, used);
        }

        var picked = SelectChunks(query, kind, remaining, perSource);
        body.Append("Transcript excerpts (the most relevant parts, not the whole recording):\n");
        (ChatSourceDocument Source, TranscriptChunk Chunk)? previous = null;
        foreach (var (source, chunk) in picked)
        {
            if (previous is { } p && !(p.Source.Alias == source.Alias && p.Chunk.Index + 1 == chunk.Index))
                body.Append("...\n");
            AppendChunk(body, source, chunk);
            used.Add((source.Alias, chunk.Start));
            previous = (source, chunk);
        }
        return Finish(body, ContextMode.Excerpts, used);
    }

    private BuiltContext Finish(StringBuilder text, ContextMode mode, List<(string, double)> used)
    {
        text.Append("\nQuestion:");
        var s = text.ToString();
        return new BuiltContext(s, mode, _count(s), used);
    }

    private static void AppendChunk(StringBuilder sb, ChatSourceDocument source, TranscriptChunk chunk)
    {
        foreach (var line in chunk.Lines)
            sb.Append(TranscriptChunker.RenderLine(source.Alias, line)).Append('\n');
    }

    internal IReadOnlyList<TranscriptChunk> ChunksOf(ChatSourceDocument source)
    {
        var key = source.RecordingId + "@" + source.VersionId;
        if (_chunkCache.TryGetValue(key, out var cached))
            return cached;
        var chunks = TranscriptChunker.Chunk(source.LoadLines(), _count);
        _chunkCache[key] = chunks;
        return chunks;
    }

    private void EnsureIndex(List<(ChatSourceDocument Source, IReadOnlyList<TranscriptChunk> Chunks)> perSource)
    {
        var key = string.Join("|", perSource.Select(p => p.Source.Alias + ":" + p.Source.RecordingId + "@" + p.Source.VersionId));
        if (key == _indexKey)
            return;
        _indexedChunks = perSource.SelectMany(p => p.Chunks.Select(c => (p.Source, c))).ToList();
        _chunkIndex = new Bm25Index(_indexedChunks.Select(x => x.Chunk.PlainText).ToList());
        _indexedLines = perSource.SelectMany(p => p.Chunks.SelectMany(c => c.Lines.Select(l => (p.Source, l)))).ToList();
        _lineIndex = new Bm25Index(_indexedLines.Select(x => x.Line.Text).ToList());
        _indexKey = key;
    }

    private static string BuildQuery(string question, string? previousQuestion, QuestionKind kind)
    {
        var query = question;
        if (kind == QuestionKind.Extraction)
            query += " " + ChatPrompts.ExtractionCues(question);
        // A short follow-up ("and who said it?") means little without the question before it.
        if (!string.IsNullOrWhiteSpace(previousQuestion) && TextTerms.Tokenize(question).Count < 4)
            query += " " + previousQuestion;
        return query;
    }

    private List<(ChatSourceDocument Source, TranscriptChunk Chunk)> SelectChunks(
        string query,
        QuestionKind kind,
        int budget,
        List<(ChatSourceDocument Source, IReadOnlyList<TranscriptChunk> Chunks)> perSource)
    {
        var chunks = _indexedChunks!;
        var scores = _chunkIndex!.Score(query);
        var order = Enumerable.Range(0, chunks.Count).Where(i => scores[i] > 0).OrderByDescending(i => scores[i]).ThenBy(i => i).ToList();

        // A pointed question needs the few best chunks, not a full window: every extra token costs prefill time
        // (about 5 ms each on a desktop CPU, much more on a phone). Keep chunks scoring close to the best one.
        if (kind == QuestionKind.Specific && order.Count > 2)
        {
            var best = scores[order[0]];
            order = order.Where((i, rank) => rank < 2 || scores[i] >= RelativeScoreCutoff * best).ToList();
        }

        // The answer often sits next to the line that names its subject ("Next, the Quiet Falcon project…" ends
        // one chunk, "Nina will book…" starts the next): the best hit is always read with its two neighbours.
        if (kind == QuestionKind.Specific && order.Count > 0)
        {
            var best = order[0];
            var priority = new List<int> { best };
            foreach (var n in new[] { best - 1, best + 1 })
            {
                if (n >= 0 && n < chunks.Count && chunks[n].Source.Alias == chunks[best].Source.Alias)
                    priority.Add(n);
            }
            priority.AddRange(order.Where(i => !priority.Contains(i)));
            order = priority;
        }

        if (order.Count == 0)
        {
            // Nothing matched ("tell me more"): spread the budget evenly over every recording.
            order = SpreadEvenly(perSource, budget);
        }

        var selected = new List<int>();
        var perSourceCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var avg = Math.Max(1, (int)chunks.Average(c => c.Chunk.Tokens));
        var slots = Math.Max(1, budget / avg);
        var cap = perSource.Count > 1 ? Math.Max(1, (slots + 1) / 2) : int.MaxValue;
        var left = budget;

        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var i in order)
            {
                if (selected.Contains(i))
                    continue;
                var (source, chunk) = chunks[i];
                if (pass == 0 && perSourceCount.GetValueOrDefault(source.Alias) >= cap)
                    continue;
                if (chunk.Tokens > left)
                    continue;
                selected.Add(i);
                perSourceCount[source.Alias] = perSourceCount.GetValueOrDefault(source.Alias) + 1;
                left -= chunk.Tokens;
                if (left < MinUsefulTokens)
                    break;
            }
            if (left < MinUsefulTokens)
                break;
        }

        // The best chunk did not fit whole (budget smaller than a chunk): cut its lines instead of sending nothing.
        if (selected.Count == 0 && order.Count > 0)
            return [TrimChunk(chunks[order[0]], budget)];

        selected.Sort();
        return selected.Select(i => chunks[i]).ToList();
    }

    private (ChatSourceDocument, TranscriptChunk) TrimChunk((ChatSourceDocument Source, TranscriptChunk Chunk) entry, int budget)
    {
        var lines = new List<TranscriptLine>();
        var tokens = 0;
        foreach (var line in entry.Chunk.Lines)
        {
            var t = _count(TranscriptChunker.RenderLine(entry.Source.Alias, line));
            if (tokens + t > budget && lines.Count > 0)
                break;
            lines.Add(line);
            tokens += t;
        }
        return (entry.Source, new TranscriptChunk(entry.Chunk.Index, lines, tokens));
    }

    private List<int> SpreadEvenly(List<(ChatSourceDocument Source, IReadOnlyList<TranscriptChunk> Chunks)> perSource, int budget)
    {
        var chunks = _indexedChunks!;
        var result = new List<int>();
        var offset = 0;
        foreach (var (_, list) in perSource)
        {
            var n = list.Count;
            var perShare = Math.Max(1, budget / perSource.Count / Math.Max(1, (int)chunks.Average(c => c.Chunk.Tokens)));
            var step = Math.Max(1, n / perShare);
            for (var i = 0; i < n; i += step)
                result.Add(offset + i);
            offset += n;
        }
        return result;
    }

    private List<(ChatSourceDocument Source, TranscriptLine Line)> SelectLines(string query, int budget, int sourceCount)
    {
        var lines = _indexedLines!;
        var scores = _lineIndex!.Score(query);

        // Questions asked: lines ending in a question mark are what the user wants.
        if (query.Contains("question", StringComparison.OrdinalIgnoreCase) || query.Contains("вопрос", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Line.Text.Contains('?'))
                    scores[i] += 2.0;
        }

        var order = Enumerable.Range(0, lines.Count).Where(i => scores[i] > 0).OrderByDescending(i => scores[i]).ThenBy(i => i);
        var selected = new List<int>();
        var perSourceCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var left = budget;
        var lineBudgetCap = sourceCount > 1 ? 0.6 : 1.0;
        var perSourceBudget = budget * lineBudgetCap;
        var perSourceUsed = new Dictionary<string, double>(StringComparer.Ordinal);
        // A sentence repeated word for word ("We decided to freeze the scope…") adds nothing after the third time.
        var seenText = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in order)
        {
            var (source, line) = lines[i];
            var key = TextTerms.Normalize(line.Text);
            if (seenText.GetValueOrDefault(key) >= MaxRepeatedLines)
                continue;
            var t = _count(TranscriptChunker.RenderLine(source.Alias, line));
            if (t > left)
                continue;
            if (perSourceUsed.GetValueOrDefault(source.Alias) + t > perSourceBudget)
                continue;
            selected.Add(i);
            seenText[key] = seenText.GetValueOrDefault(key) + 1;
            perSourceUsed[source.Alias] = perSourceUsed.GetValueOrDefault(source.Alias) + t;
            perSourceCount[source.Alias] = perSourceCount.GetValueOrDefault(source.Alias) + 1;
            left -= t;
            if (left < 20)
                break;
        }
        selected.Sort();
        return selected.Select(i => lines[i]).ToList();
    }
}
