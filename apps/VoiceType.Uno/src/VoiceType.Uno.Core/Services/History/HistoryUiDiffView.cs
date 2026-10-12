using System.Globalization;

namespace VoiceType.Uno.History;

public enum HistoryDiffRunKind
{
    Same,
    /// <summary>Text of version A that is gone in B (shown struck through).</summary>
    Deleted,
    /// <summary>Text new in B.</summary>
    Inserted,
}

public sealed record HistoryDiffRun(HistoryDiffRunKind Kind, string Text);

/// <summary>One segment of version B with its words split into same / deleted / inserted runs.</summary>
public sealed record HistoryDiffSegment(int Index, double Start, string TimeText, IReadOnlyList<HistoryDiffRun> Runs)
{
    public bool HasChanges => Runs.Any(r => r.Kind != HistoryDiffRunKind.Same);
}

/// <summary>Diff of two versions laid out for the Versions › Compare view.</summary>
public sealed record HistoryDiffView(IReadOnlyList<HistoryDiffSegment> Segments, string StatsText, bool IsIdentical)
{
    public IReadOnlyList<HistoryDiffSegment> ChangedSegments => Segments.Where(s => s.HasChanges).ToList();
}

/// <summary>
/// Turns <see cref="TranscriptDiff"/> hunks into per-segment runs of version B: unchanged
/// words, deleted words of A (anchored like <see cref="TranscriptDiff"/> anchors them) and
/// inserted words of B. Adjacent runs of the same kind are merged.
/// </summary>
public static class HistoryUiDiff
{
    public static HistoryDiffView Build(TranscriptDocument a, TranscriptDocument b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var result = TranscriptDiff.Compute(a, b);
        var runs = new List<HistoryDiffRun>[b.Segments.Count];
        for (var i = 0; i < runs.Length; i++)
            runs[i] = [];

        void Add(int segment, HistoryDiffRunKind kind, string text)
        {
            if (segment < 0 || segment >= runs.Length || text.Length == 0)
                return;
            var list = runs[segment];
            if (list.Count > 0 && list[^1].Kind == kind)
                list[^1] = list[^1] with { Text = list[^1].Text + " " + text };
            else
                list.Add(new HistoryDiffRun(kind, text));
        }

        foreach (var hunk in result.Hunks)
        {
            if (hunk.Kind == DiffKind.Equal)
            {
                for (var k = 0; k < hunk.BCount; k++)
                {
                    var token = result.BTokens[hunk.BStart + k];
                    Add(token.SegmentIndex, HistoryDiffRunKind.Same, token.Text);
                }
                continue;
            }

            var anchor = Anchor(hunk, result.BTokens);
            if (hunk.ACount > 0)
                Add(anchor, HistoryDiffRunKind.Deleted,
                    string.Join(" ", result.ATokens.Skip(hunk.AStart).Take(hunk.ACount).Select(t => t.Text)));
            for (var k = 0; k < hunk.BCount; k++)
            {
                var token = result.BTokens[hunk.BStart + k];
                Add(token.SegmentIndex, HistoryDiffRunKind.Inserted, token.Text);
            }
        }

        var segments = b.Segments
            .Select((s, i) => new HistoryDiffSegment(s.Index, s.Start, HistoryUiFormat.SegmentTime(s.Start), runs[i]))
            .Where(s => s.Runs.Count > 0)
            .ToList();

        return new HistoryDiffView(segments, Stats(result), result.IsIdentical);
    }

    /// <summary><c>7 words changed in 5 of 14 segments · 3.4% of words</c>.</summary>
    public static string Stats(TranscriptDiffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsIdentical)
            return "No word changes";
        var segmentsB = result.BTokens.Select(t => t.SegmentIndex).Distinct().Count();
        var percent = result.ChangedPercent.ToString(result.ChangedPercent >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return $"{HistoryUiFormat.Count(result.WordsChanged, "word")} changed in {result.SegmentsChanged} of " +
               $"{HistoryUiFormat.Count(segmentsB, "segment")} · {percent}% of words";
    }

    // Same rule as TranscriptDiff: a replacement/insertion belongs to the segment of its
    // first new word; a pure deletion to the word before it (or after it at the start).
    private static int Anchor(DiffHunk hunk, IReadOnlyList<DiffToken> bTokens)
    {
        if (hunk.BCount > 0)
            return bTokens[hunk.BStart].SegmentIndex;
        if (hunk.BStart > 0)
            return bTokens[hunk.BStart - 1].SegmentIndex;
        if (hunk.BStart < bTokens.Count)
            return bTokens[hunk.BStart].SegmentIndex;
        return -1;
    }
}
