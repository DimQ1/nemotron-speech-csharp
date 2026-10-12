using System.Text;
using System.Text.RegularExpressions;

namespace SpeechLib.Agents;

/// <summary>A validated reference from an answer into a recording.</summary>
public sealed record ParsedCitation(string Alias, string RecordingId, string VersionId, double StartSec, double? EndSec, string Quote, string? Speaker = null);

/// <summary>The answer text with invalid citation markers removed, plus the citations that survived validation.</summary>
public sealed record ParsedAnswer(string Text, IReadOnlyList<ParsedCitation> Citations);

/// <summary>
/// Turns <c>[S2 03:20]</c> (also <c>[S2 03:20-04:10]</c>, <c>[S1 03:20, 04:10]</c>, <c>(S1 03:20)</c>, and bare
/// <c>[03:20]</c> when the chat has exactly one active source) into (recording, seconds). A citation survives only
/// when its alias belongs to an active source and the time falls inside that source's transcript; its time is
/// snapped to the start of the segment that contains it. Everything else is dropped from text and list.
/// </summary>
public static partial class CitationParser
{
    [GeneratedRegex(@"[\[\(]([^\[\]\(\)\r\n]{1,80})[\]\)]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"(?:(?<alias>S\d+)[\s,:;]*)?(?<t1>\d{1,3}:\d{2}(?::\d{2})?)(?:\s*[-–—]\s*(?<t2>\d{1,3}:\d{2}(?::\d{2})?))?", RegexOptions.IgnoreCase)]
    private static partial Regex ItemRegex();

    [GeneratedRegex(@"^\s*S\d+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareAliasRegex();

    private const double Slack = 2.0;
    private const int QuoteLimit = 160;

    [GeneratedRegex(@"no information|not mentioned|not discussed|does not (contain|mention|say)|doesn't (contain|mention|say)|there is no|нет информации|не упомина|не говор\w+ о|не обсужда|нет данных", RegexOptions.IgnoreCase)]
    private static partial Regex NoAnswerRegex();

    /// <summary>A short "it is not in the transcript" reply: it points at nothing, so it carries no citations.</summary>
    public static bool LooksLikeNoAnswer(string text) => text.Length < 260 && NoAnswerRegex().IsMatch(text);

    public static ParsedAnswer Parse(string text, IReadOnlyList<ChatSourceDocument> sources)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sources);
        if (LooksLikeNoAnswer(text))
            sources = [];
        var active = sources.Where(s => s.Active).ToList();
        var lines = new Dictionary<string, IReadOnlyList<TranscriptLine>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<TranscriptLine> LinesOf(ChatSourceDocument source)
        {
            if (!lines.TryGetValue(source.Alias, out var l))
                lines[source.Alias] = l = source.LoadLines();
            return l;
        }

        var citations = new List<ParsedCitation>();
        var seen = new HashSet<(string, long)>();
        var output = new StringBuilder();
        var last = 0;

        foreach (Match match in BracketRegex().Matches(text))
        {
            var content = match.Groups[1].Value;
            var items = ItemRegex().Matches(content);
            var isCitationShaped = items.Count > 0 && CoversOnlySeparators(content, items);
            var isBareAlias = !isCitationShaped && BareAliasRegex().IsMatch(content);
            if (!isCitationShaped && !isBareAlias)
                continue;

            output.Append(text, last, match.Index - last);
            var last0 = last;
            last = match.Index + match.Length;

            if (isBareAlias)
            {
                TrimTrailingSpace(output);
                continue;
            }

            var rendered = new List<string>();
            var currentAlias = (string?)null;
            foreach (Match item in items)
            {
                if (item.Groups["alias"].Success)
                    currentAlias = item.Groups["alias"].Value.ToUpperInvariant();

                var source = ResolveSource(active, currentAlias);
                if (source is null || !TimeText.TryParse(item.Groups["t1"].Value, out var t1))
                    continue;

                var segmentLines = LinesOf(source);
                if (!Snap(segmentLines, t1, out var snapped))
                    continue;
                snapped = Refine(segmentLines, snapped, t1, ClaimBefore(text, match.Index, last0));

                double? end = snapped.End;
                if (item.Groups["t2"].Success && TimeText.TryParse(item.Groups["t2"].Value, out var t2) && t2 > snapped.Start
                    && Snap(segmentLines, t2, out var snappedEnd))
                    end = snappedEnd.End;

                var quote = snapped.Text.Trim();
                if (quote.Length > QuoteLimit)
                    quote = quote[..QuoteLimit].TrimEnd() + "…";

                rendered.Add($"[{source.Alias} {TimeText.Format(snapped.Start)}]");
                if (seen.Add((source.Alias, (long)Math.Round(snapped.Start * 100))))
                    citations.Add(new ParsedCitation(source.Alias, source.RecordingId, source.VersionId, snapped.Start, end, quote, snapped.Speaker));
            }

            if (rendered.Count > 0)
                output.Append(string.Join(' ', rendered));
            else
                TrimTrailingSpace(output);
        }
        output.Append(text, last, text.Length - last);

        return new ParsedAnswer(Tidy(output.ToString()), citations);
    }

    private const double RefineWindow = 12.0;

    /// <summary>The sentence (or bullet) the marker belongs to: from the previous marker / sentence end up to the marker.</summary>
    private static string ClaimBefore(string text, int markerIndex, int previousEnd)
    {
        var start = Math.Max(0, previousEnd);
        var span = text.AsSpan(start, markerIndex - start);
        var cut = span.LastIndexOfAny('\n', '\r');
        foreach (var terminator in new[] { ". ", "! ", "? " })
        {
            var i = span.LastIndexOf(terminator.AsSpan());
            if (i > cut && i < span.Length - 2)
                cut = i + 1;
        }
        return span[(cut < 0 ? 0 : cut + 1)..].ToString();
    }

    /// <summary>
    /// Small models cite a time near the right line rather than exactly. Among the lines within a few seconds of
    /// the cited time, the one that shares the most content words with the claim wins; with no clear winner the
    /// snapped line stays.
    /// </summary>
    private static TranscriptLine Refine(IReadOnlyList<TranscriptLine> lines, TranscriptLine snapped, double t, string claim)
    {
        var claimTerms = new HashSet<string>(TextTerms.Tokenize(claim), StringComparer.Ordinal);
        if (claimTerms.Count < 2)
            return snapped;

        int Shared(TranscriptLine l) => TextTerms.Tokenize(l.Text).Distinct(StringComparer.Ordinal).Count(claimTerms.Contains);

        var best = snapped;
        var bestShared = Shared(snapped);
        foreach (var line in lines)
        {
            if (line.Start < t - RefineWindow || line.Start > t + RefineWindow || ReferenceEquals(line, snapped))
                continue;
            var shared = Shared(line);
            if (shared >= 2 && shared > bestShared)
            {
                best = line;
                bestShared = shared;
            }
        }
        return best;
    }

    private static ChatSourceDocument? ResolveSource(List<ChatSourceDocument> active, string? alias)
    {
        if (alias is not null)
            return active.Find(s => string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase));
        return active.Count == 1 ? active[0] : null;
    }

    private static bool CoversOnlySeparators(string content, MatchCollection items)
    {
        var pos = 0;
        foreach (Match item in items)
        {
            if (!IsSeparators(content.AsSpan(pos, item.Index - pos)))
                return false;
            pos = item.Index + item.Length;
        }
        return IsSeparators(content.AsSpan(pos));
    }

    private static bool IsSeparators(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!(char.IsWhiteSpace(c) || c is ',' or ';' or '&'))
                return false;
        }
        return true;
    }

    private static bool Snap(IReadOnlyList<TranscriptLine> lines, double t, out TranscriptLine snapped)
    {
        snapped = null!;
        if (lines.Count == 0)
            return false;
        if (t < lines[0].Start - Slack || t > lines[^1].End + Slack)
            return false;

        var best = lines[0];
        foreach (var line in lines)
        {
            if (line.Start <= t + 0.5)
                best = line;
            else
                break;
        }
        snapped = best;
        return true;
    }

    private static void TrimTrailingSpace(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
    }

    private static string Tidy(string text)
    {
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @" +([.,;:!?])", "$1");
        return text.Trim();
    }
}
