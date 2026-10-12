namespace VoiceType.Uno.History;

/// <summary>A word of a diffed transcript: original text, comparison key and segment.</summary>
public readonly record struct DiffToken(string Text, string Key, int SegmentIndex);

/// <summary>One change inside a segment of version B.</summary>
public sealed record WordChange(DiffKind Kind, string OldText, string NewText, int BTokenStart);

/// <summary>Changes anchored to one segment (list position) of version B.</summary>
public sealed record SegmentDiff(
    int SegmentIndex,
    IReadOnlyList<WordChange> Changes,
    int Substitutions,
    int Deletions,
    int Insertions);

public sealed record TranscriptDiffResult(
    IReadOnlyList<DiffToken> ATokens,
    IReadOnlyList<DiffToken> BTokens,
    IReadOnlyList<DiffHunk> Hunks,
    IReadOnlyList<SegmentDiff> Segments,
    int Substitutions,
    int Deletions,
    int Insertions)
{
    /// <summary>Words of version A (the reference for the percentage).</summary>
    public int ReferenceWords => ATokens.Count;

    /// <summary>S + D + I.</summary>
    public int WordsChanged => Substitutions + Deletions + Insertions;

    /// <summary>Segments of B with at least one change.</summary>
    public int SegmentsChanged => Segments.Count;

    /// <summary>(S + D + I) / N × 100 relative to A — a WER of B against A, shown as "diff".</summary>
    public double ChangedPercent => ReferenceWords == 0
        ? (WordsChanged > 0 ? 100.0 : 0.0)
        : 100.0 * WordsChanged / ReferenceWords;

    public bool IsIdentical => WordsChanged == 0;
}

/// <summary>
/// Word-level diff of two transcript versions on normalised tokens (case-folded,
/// diacritics stripped, punctuation ignored), with changes anchored to the segments
/// of version B.
/// </summary>
public static class TranscriptDiff
{
    public static TranscriptDiffResult Compute(TranscriptDocument a, TranscriptDocument b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var aTokens = Tokenize(a);
        var bTokens = Tokenize(b);
        var hunks = WordAligner.Align(
            aTokens.Select(t => t.Key).ToList(),
            bTokens.Select(t => t.Key).ToList());

        int subs = 0, dels = 0, ins = 0;
        var perSegment = new SortedDictionary<int, List<(WordChange Change, int S, int D, int I)>>();

        foreach (var hunk in hunks)
        {
            if (hunk.Kind == DiffKind.Equal)
                continue;

            var s = Math.Min(hunk.ACount, hunk.BCount);
            var d = hunk.ACount - s;
            var i = hunk.BCount - s;
            subs += s;
            dels += d;
            ins += i;

            var segment = AnchorSegment(hunk, bTokens);
            var change = new WordChange(
                hunk.Kind,
                JoinText(aTokens, hunk.AStart, hunk.ACount),
                JoinText(bTokens, hunk.BStart, hunk.BCount),
                hunk.BStart);
            if (!perSegment.TryGetValue(segment, out var list))
                perSegment[segment] = list = [];
            list.Add((change, s, d, i));
        }

        var segments = perSegment
            .Select(kv => new SegmentDiff(
                kv.Key,
                kv.Value.Select(x => x.Change).ToList(),
                kv.Value.Sum(x => x.S),
                kv.Value.Sum(x => x.D),
                kv.Value.Sum(x => x.I)))
            .ToList();

        return new TranscriptDiffResult(aTokens, bTokens, hunks, segments, subs, dels, ins);
    }

    internal static List<DiffToken> Tokenize(TranscriptDocument document)
    {
        var tokens = new List<DiffToken>();
        for (var s = 0; s < document.Segments.Count; s++)
        {
            foreach (var word in TextTokens.SplitWords(document.Segments[s].Text))
            {
                var key = TextNormalizer.NormalizeToken(word);
                if (key.Length > 0)
                    tokens.Add(new DiffToken(word, key, s));
            }
        }
        return tokens;
    }

    // Inserted/replaced words belong to the segment of their first new word; a pure
    // deletion is attached to the segment of the word before it (or after it at the start).
    private static int AnchorSegment(DiffHunk hunk, List<DiffToken> bTokens)
    {
        if (hunk.BCount > 0)
            return bTokens[hunk.BStart].SegmentIndex;
        if (hunk.BStart > 0)
            return bTokens[hunk.BStart - 1].SegmentIndex;
        if (hunk.BStart < bTokens.Count)
            return bTokens[hunk.BStart].SegmentIndex;
        return -1;
    }

    private static string JoinText(List<DiffToken> tokens, int start, int count) =>
        string.Join(" ", tokens.Skip(start).Take(count).Select(t => t.Text));
}

/// <summary>
/// Builds an edited transcript version from user text while keeping the timings of
/// unchanged words; changed and inserted words get timings interpolated inside
/// their segment.
/// </summary>
public static class TranscriptTimings
{
    /// <summary>
    /// Per-segment edit: <paramref name="editedSegmentTexts"/>[i] replaces segment i of
    /// <paramref name="source"/>. Segments edited to empty text are dropped.
    /// </summary>
    public static TranscriptDocument CarryOver(
        TranscriptDocument source, IReadOnlyList<string> editedSegmentTexts, string newVersionId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(editedSegmentTexts);
        if (editedSegmentTexts.Count != source.Segments.Count)
            throw new ArgumentException(
                $"Expected {source.Segments.Count} segment texts, got {editedSegmentTexts.Count}.",
                nameof(editedSegmentTexts));

        var groups = new List<(TranscriptSegment Source, IReadOnlyList<string> Words)>();
        for (var i = 0; i < source.Segments.Count; i++)
            groups.Add((source.Segments[i], TextTokens.SplitWords(editedSegmentTexts[i])));
        return Build(groups, newVersionId);
    }

    /// <summary>
    /// Whole-text edit (e.g. the live text box): the new words are aligned against all
    /// source words and each lands in the segment of the source word it matches.
    /// </summary>
    public static TranscriptDocument CarryOver(TranscriptDocument source, string editedText, string newVersionId)
    {
        ArgumentNullException.ThrowIfNull(source);

        var newWords = TextTokens.SplitWords(editedText);
        var groups = source.Segments
            .Select(s => (Source: s, Words: (IReadOnlyList<string>)new List<string>()))
            .ToList();
        if (groups.Count == 0)
        {
            if (newWords.Count == 0)
                return Build([], newVersionId);
            groups.Add((new TranscriptSegment(), new List<string>()));
        }

        var flat = new List<(string Key, int Segment)>();
        for (var s = 0; s < source.Segments.Count; s++)
            foreach (var w in SourceWords(source.Segments[s]))
                flat.Add((AlignKey(w.Text), s));

        var hunks = WordAligner.Align(flat.Select(f => f.Key).ToList(), newWords.Select(AlignKey).ToList());
        foreach (var hunk in hunks)
        {
            for (var j = 0; j < hunk.BCount; j++)
            {
                int segment;
                if (hunk.ACount > 0)
                {
                    // Equal: one-to-one; replace: spread proportionally over the old words.
                    var offset = hunk.Kind == DiffKind.Equal ? j : Math.Min(j * hunk.ACount / hunk.BCount, hunk.ACount - 1);
                    segment = flat[hunk.AStart + offset].Segment;
                }
                else if (hunk.AStart > 0)
                    segment = flat[hunk.AStart - 1].Segment;
                else if (hunk.AStart < flat.Count)
                    segment = flat[hunk.AStart].Segment;
                else
                    segment = 0;
                ((List<string>)groups[segment].Words).Add(newWords[hunk.BStart + j]);
            }
        }

        return Build(groups, newVersionId);
    }

    private static TranscriptDocument Build(
        List<(TranscriptSegment Source, IReadOnlyList<string> Words)> groups, string newVersionId)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var (src, words) in groups)
        {
            if (words.Count == 0)
                continue;

            var (timed, changed) = AlignSegmentWords(src, words);
            segments.Add(new TranscriptSegment
            {
                Index = segments.Count,
                Start = src.Start,
                End = src.End,
                Text = string.Join(" ", words),
                Words = timed,
                WordTiming = changed ? WordTimingKinds.Estimated : src.WordTiming,
            });
        }

        return new TranscriptDocument
        {
            Version = newVersionId,
            Segments = segments,
            Text = TranscriptDocument.JoinSegments(segments),
        };
    }

    /// <summary>Source words with timings; estimated by character length when missing or stale.</summary>
    internal static List<TranscriptWord> SourceWords(TranscriptSegment segment)
    {
        var textWords = TextTokens.SplitWords(segment.Text);
        if (segment.Words.Count == textWords.Count && segment.Words.Count > 0)
            return segment.Words;
        return Distribute(textWords, segment.Start, segment.End);
    }

    internal static (List<TranscriptWord> Words, bool Changed) AlignSegmentWords(
        TranscriptSegment segment, IReadOnlyList<string> newWords)
    {
        var src = SourceWords(segment);
        var hunks = WordAligner.Align(src.Select(w => AlignKey(w.Text)).ToList(), newWords.Select(AlignKey).ToList());
        var result = new List<TranscriptWord>(newWords.Count);
        var changed = false;

        foreach (var hunk in hunks)
        {
            if (hunk.Kind == DiffKind.Equal)
            {
                for (var j = 0; j < hunk.BCount; j++)
                {
                    var old = src[hunk.AStart + j];
                    var text = newWords[hunk.BStart + j];
                    if (text != old.Text)
                        changed = true;
                    result.Add(new TranscriptWord(text, old.Start, old.End));
                }
                continue;
            }

            changed = true;
            if (hunk.BCount == 0)
                continue;

            // Replaced words take over the span of the words they replace; inserted
            // words fill the gap between the previous and the next kept word.
            var prevEnd = result.Count > 0 ? result[^1].End : segment.Start;
            var nextIndex = hunk.AStart + hunk.ACount;
            var nextStart = nextIndex < src.Count ? src[nextIndex].Start : segment.End;
            double left, right;
            if (hunk.ACount > 0)
            {
                left = Math.Max(prevEnd, src[hunk.AStart].Start);
                right = Math.Min(nextStart, src[nextIndex - 1].End);
            }
            else
            {
                left = prevEnd;
                right = nextStart;
            }
            if (right < left)
                right = left;

            result.AddRange(Distribute(newWords.Skip(hunk.BStart).Take(hunk.BCount).ToList(), left, right));
        }

        return (result, changed);
    }

    /// <summary>Spreads <paramref name="words"/> over [start, end] proportionally to their length.</summary>
    internal static List<TranscriptWord> Distribute(IReadOnlyList<string> words, double start, double end)
    {
        var result = new List<TranscriptWord>(words.Count);
        if (words.Count == 0)
            return result;

        var total = words.Sum(w => Math.Max(1, w.Length));
        var span = Math.Max(0, end - start);
        var t = start;
        var acc = 0;
        for (var i = 0; i < words.Count; i++)
        {
            acc += Math.Max(1, words[i].Length);
            var e = i == words.Count - 1 ? end : start + span * acc / total;
            result.Add(new TranscriptWord(words[i], Round(t), Round(Math.Max(t, e))));
            t = e;
        }
        return result;
    }

    private static string AlignKey(string word)
    {
        var key = TextNormalizer.NormalizeToken(word);
        return key.Length > 0 ? key : word;
    }

    private static double Round(double value) => Math.Round(value, 3);
}
