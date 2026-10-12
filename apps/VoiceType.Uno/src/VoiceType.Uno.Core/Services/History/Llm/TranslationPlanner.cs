using System.Text;

namespace VoiceType.Uno.History;

/// <summary>
/// One sentence of a saved transcript, with the segments it covers. Segment numbers are
/// <see cref="TranscriptSegment.Index"/> values.
/// </summary>
/// <param name="Index">Position in the plan (0-based); sentences are translated in this order.</param>
/// <param name="StartSegment">Segment the sentence starts in: its translation is shown there.</param>
/// <param name="EndSegment">Segment the sentence ends in (same as <paramref name="StartSegment"/> unless it spans several).</param>
/// <param name="Tokens">Token count of <paramref name="Text"/>.</param>
public sealed record PlannedSentence(int Index, int StartSegment, int EndSegment, string Text, int Tokens);

/// <summary>A run of consecutive sentences translated and saved together (≈ 500–800 prompt tokens).</summary>
/// <param name="FirstSentence">Index of the first sentence.</param>
/// <param name="Count">Number of sentences.</param>
/// <param name="PromptTokens">Estimated prompt tokens of all requests of the unit.</param>
public sealed record TranslationUnit(int FirstSentence, int Count, int PromptTokens);

/// <summary>
/// Cuts a saved transcript into sentences (keeping their segments) and groups them into units
/// (design §8). Pure and deterministic: the same segments always give the same plan, which is what
/// lets a partial translation continue after a pause, a preemption or a killed app.
/// </summary>
public static class TranslationPlanner
{
    /// <summary>A sentence longer than this many tokens (no terminator for a long time) is cut at word boundaries.</summary>
    public const int DefaultMaxSentenceTokens = 160;

    /// <summary>Prompt tokens a request adds on top of the sentence (chat template, role markers).</summary>
    public const int RequestOverheadTokens = 32;

    private static readonly char[] Terminators = ['.', '!', '?', '…', '。', '！', '？'];

    /// <summary>The sentences of <paramref name="segments"/> in order, each pointing at the segment it starts in.</summary>
    public static List<PlannedSentence> Split(
        IReadOnlyList<TranscriptSegment> segments, Func<string, int> countTokens, int maxSentenceTokens = DefaultMaxSentenceTokens)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(countTokens);

        // One text with every segment trimmed and joined by a space; remember where each starts.
        var text = new StringBuilder();
        var starts = new List<(int Offset, int SegmentIndex)>();
        foreach (var segment in segments)
        {
            var part = segment.Text?.Trim() ?? "";
            if (part.Length == 0)
                continue;
            if (text.Length > 0)
                text.Append(' ');
            starts.Add((text.Length, segment.Index));
            text.Append(part);
        }

        var all = text.ToString();
        var result = new List<PlannedSentence>();
        if (all.Length == 0)
            return result;

        int SegmentAt(int offset)
        {
            int lo = 0, hi = starts.Count - 1, found = 0;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (starts[mid].Offset <= offset)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return starts[found].SegmentIndex;
        }

        void Add(int from, int to)
        {
            // Trim, drop a leading dash or ellipsis, and skip pieces with no letters or digits ("…", "-").
            while (from < to && (char.IsWhiteSpace(all[from]) || all[from] is '-' or '–' or '—' or '…' or '.' or ','))
                from++;
            while (to > from && char.IsWhiteSpace(all[to - 1]))
                to--;
            if (to <= from)
                return;
            var sentence = all.AsSpan(from, to - from);
            var hasContent = false;
            foreach (var c in sentence)
            {
                if (char.IsLetterOrDigit(c))
                {
                    hasContent = true;
                    break;
                }
            }
            if (!hasContent)
                return;

            var tokens = countTokens(sentence.ToString());
            if (tokens <= maxSentenceTokens)
            {
                result.Add(new PlannedSentence(result.Count, SegmentAt(from), SegmentAt(to - 1), sentence.ToString(), tokens));
                return;
            }

            // Long without a terminator: equal pieces cut at word boundaries (commas preferred).
            var pieces = (int)Math.Ceiling(tokens / (double)maxSentenceTokens);
            var target = Math.Max(1, (to - from) / pieces);
            var pieceStart = from;
            while (pieceStart < to)
            {
                var pieceEnd = to - pieceStart <= target * 1.3 ? to : CutPoint(all, pieceStart, target, to);
                var piece = all[pieceStart..pieceEnd].Trim();
                if (piece.Length > 0)
                    result.Add(new PlannedSentence(result.Count, SegmentAt(pieceStart), SegmentAt(pieceEnd - 1), piece, countTokens(piece)));
                pieceStart = pieceEnd;
            }
        }

        var begin = 0;
        for (var i = 0; i < all.Length; i++)
        {
            if (Array.IndexOf(Terminators, all[i]) < 0)
                continue;
            // "3.14" and "Mr. Smith" style: a terminator closes a sentence only before whitespace or the end.
            var next = i + 1;
            var fullWidth = all[i] is '。' or '！' or '？'; // CJK text has no space after a sentence
            if (!fullWidth && next < all.Length && !char.IsWhiteSpace(all[next]) && all[next] is not ('"' or '”' or '»' or ')' or '\''))
                continue;
            while (next < all.Length && all[next] is '"' or '”' or '»' or ')' or '\'')
                next++;
            Add(begin, next);
            begin = next;
            i = next - 1;
        }
        Add(begin, all.Length);
        return result;
    }

    /// <summary>Cut near <paramref name="start"/> + <paramref name="target"/>: after a comma in the last third, else at a space.</summary>
    private static int CutPoint(string text, int start, int target, int limit)
    {
        var want = Math.Min(limit, start + target);
        var floor = start + target * 2 / 3;
        for (var i = want; i > floor; i--)
        {
            if (text[i - 1] is ',' or ';' or ':' or '—')
                return i;
        }
        for (var i = want; i > start + 1; i--)
        {
            if (char.IsWhiteSpace(text[i - 1]))
                return i;
        }
        return want;
    }

    /// <summary>Estimated prompt tokens of the request for <paramref name="sentences"/>[i], with the previous pair as context.</summary>
    public static int EstimatePromptTokens(IReadOnlyList<PlannedSentence> sentences, int i, int systemPromptTokens)
    {
        var cost = systemPromptTokens + RequestOverheadTokens + sentences[i].Tokens;
        if (i > 0)
            cost += sentences[i - 1].Tokens * 2; // previous source and (similar length) previous translation
        return cost;
    }

    /// <summary>
    /// Groups sentences from <paramref name="from"/> into units that stay within
    /// <paramref name="maxPromptTokens"/> prompt tokens (typically 3–5 sentences, ≈ 500–800 tokens);
    /// every unit has at least one sentence, so one huge sentence is a unit of its own.
    /// </summary>
    public static List<TranslationUnit> PlanUnits(
        IReadOnlyList<PlannedSentence> sentences, int from, int systemPromptTokens, int maxPromptTokens = 800)
    {
        var units = new List<TranslationUnit>();
        var i = Math.Max(0, from);
        while (i < sentences.Count)
        {
            var first = i;
            var sum = 0;
            while (i < sentences.Count)
            {
                var cost = EstimatePromptTokens(sentences, i, systemPromptTokens);
                if (i > first && sum + cost > maxPromptTokens)
                    break;
                sum += cost;
                i++;
            }
            units.Add(new TranslationUnit(first, i - first, sum));
        }
        return units;
    }
}
