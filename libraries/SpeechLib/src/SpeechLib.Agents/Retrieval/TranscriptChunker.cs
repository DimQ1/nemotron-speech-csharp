using System.Globalization;
using System.Text;

namespace SpeechLib.Agents;

/// <summary>One timed piece of a transcript (a segment of the recording).</summary>
/// <param name="Speaker">Optional speaker name; rendered as a "Name: " prefix. Left for the diarization work.</param>
public sealed record TranscriptLine(double Start, double End, string Text, string? Speaker = null);

/// <summary>A run of whole segments of about 300–500 tokens: the unit of retrieval and of map summarisation.</summary>
public sealed class TranscriptChunk
{
    public TranscriptChunk(int index, IReadOnlyList<TranscriptLine> lines, int tokens)
    {
        Index = index;
        Lines = lines;
        Tokens = tokens;
    }

    public int Index { get; }
    public IReadOnlyList<TranscriptLine> Lines { get; }

    /// <summary>Tokens of the chunk as rendered in a prompt (timestamp prefix per line included).</summary>
    public int Tokens { get; }

    public double Start => Lines[0].Start;
    public double End => Lines[^1].End;

    /// <summary>Plain text without timestamps (for indexing and summarising).</summary>
    public string PlainText => string.Join(' ', Lines.Select(l => l.Text));
}

/// <summary>Time formatting shared by prompts, citations and the UI.</summary>
public static class TimeText
{
    /// <summary><c>mm:ss</c>, or <c>h:mm:ss</c> from one hour on.</summary>
    public static string Format(double seconds)
    {
        var total = (long)Math.Max(0, Math.Floor(seconds));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}");
    }

    /// <summary>Parses <c>mm:ss</c> or <c>h:mm:ss</c>.</summary>
    public static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        var values = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }
        if (values[^1] > 59 || (parts.Length == 3 && values[1] > 59))
            return false;
        seconds = parts.Length == 3 ? values[0] * 3600 + values[1] * 60 + values[2] : values[0] * 60 + values[1];
        return true;
    }
}

public static class TranscriptChunker
{
    public const int DefaultTargetTokens = 400;
    public const int DefaultMaxTokens = 500;

    /// <summary>The line the way prompts show it: <c>[S1 03:20] text</c>.</summary>
    public static string RenderLine(string alias, TranscriptLine line)
    {
        var text = line.Text.Trim();
        var body = string.IsNullOrWhiteSpace(line.Speaker) ? text : line.Speaker + ": " + text;
        return $"[{alias} {TimeText.Format(line.Start)}] {body}";
    }

    /// <summary>
    /// Greedy segment-aligned chunking. A chunk closes at <paramref name="targetTokens"/> or earlier when the
    /// next segment would pass <paramref name="maxTokens"/>. A segment longer than the maximum is split at
    /// sentence (then word) boundaries with its time range divided in proportion to the text.
    /// </summary>
    public static IReadOnlyList<TranscriptChunk> Chunk(
        IReadOnlyList<TranscriptLine> lines,
        Func<string, int> countTokens,
        int targetTokens = DefaultTargetTokens,
        int maxTokens = DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(countTokens);

        var perLineLimit = Math.Max(40, maxTokens * 3 / 5);
        var chunks = new List<TranscriptChunk>();
        var current = new List<TranscriptLine>();
        var currentTokens = 0;

        void Close()
        {
            if (current.Count == 0)
                return;
            chunks.Add(new TranscriptChunk(chunks.Count, current.ToArray(), currentTokens));
            current = [];
            currentTokens = 0;
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw.Text))
                continue;
            foreach (var line in SplitLong(raw, countTokens, perLineLimit))
            {
                var tokens = countTokens(RenderLine("S1", line));
                if (current.Count > 0 && currentTokens + tokens > maxTokens)
                    Close();
                current.Add(line);
                currentTokens += tokens;
                if (currentTokens >= targetTokens)
                    Close();
            }
        }
        Close();

        // A tiny tail is folded into the previous chunk when that stays within the maximum.
        if (chunks.Count >= 2)
        {
            var last = chunks[^1];
            var prev = chunks[^2];
            if (last.Tokens < targetTokens / 3 && prev.Tokens + last.Tokens <= maxTokens)
            {
                chunks.RemoveRange(chunks.Count - 2, 2);
                chunks.Add(new TranscriptChunk(chunks.Count, prev.Lines.Concat(last.Lines).ToArray(), prev.Tokens + last.Tokens));
            }
        }
        return chunks;
    }

    private static IEnumerable<TranscriptLine> SplitLong(TranscriptLine line, Func<string, int> countTokens, int limit)
    {
        if (countTokens(RenderLine("S1", line)) <= limit)
        {
            yield return line;
            yield break;
        }

        var pieces = new List<string>();
        var buffer = new StringBuilder();
        foreach (var sentence in SplitSentences(line.Text))
        {
            foreach (var part in SplitWords(sentence, countTokens, limit))
            {
                if (buffer.Length > 0 && countTokens(RenderLine("S1", line with { Text = buffer + " " + part })) > limit)
                {
                    pieces.Add(buffer.ToString());
                    buffer.Clear();
                }
                if (buffer.Length > 0)
                    buffer.Append(' ');
                buffer.Append(part);
            }
        }
        if (buffer.Length > 0)
            pieces.Add(buffer.ToString());

        var totalChars = Math.Max(1, pieces.Sum(p => p.Length));
        var duration = Math.Max(0, line.End - line.Start);
        var offset = 0;
        foreach (var piece in pieces)
        {
            var start = line.Start + duration * offset / totalChars;
            offset += piece.Length;
            var end = line.Start + duration * offset / totalChars;
            yield return line with { Start = start, End = end, Text = piece };
        }
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' or '…' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                var s = text[start..(i + 1)].Trim();
                if (s.Length > 0)
                    yield return s;
                start = i + 1;
            }
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private static IEnumerable<string> SplitWords(string sentence, Func<string, int> countTokens, int limit)
    {
        if (countTokens(sentence) + 8 <= limit)
        {
            yield return sentence;
            yield break;
        }
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var buffer = new StringBuilder();
        foreach (var word in words)
        {
            if (buffer.Length > 0 && countTokens(buffer + " " + word) + 8 > limit)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
            if (buffer.Length > 0)
                buffer.Append(' ');
            buffer.Append(word);
        }
        if (buffer.Length > 0)
            yield return buffer.ToString();
    }
}
