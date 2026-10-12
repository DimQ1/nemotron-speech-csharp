using System.Text;
using System.Text.RegularExpressions;

namespace SpeechLib.LiteRT.Native;

/// <summary>
/// Cleans the start of a continued answer so it joins the text the caller already received. Measured on the real model:
/// asked to "continue", Gemma often restarts the unfinished sentence ("The cycle begins in" + "The cycle begins in the
/// evaporator") or opens with an ellipsis. The first few characters are held back until that can be decided, then the
/// repeated overlap and a leading ellipsis are dropped and the rest streams through untouched.
/// </summary>
internal sealed partial class ContinuationJoiner
{
    private const int MinOverlap = 6;
    private const int MaxTail = 64;

    private readonly string _tail;
    private readonly bool _partialEndsWithSpace;
    private readonly int _lookahead;
    private readonly StringBuilder _pending = new();
    private bool _decided;

    public ContinuationJoiner(string partial)
    {
        _tail = partial.Length > MaxTail ? partial[^MaxTail..] : partial;
        _partialEndsWithSpace = partial.Length == 0 || char.IsWhiteSpace(partial[^1]);
        _lookahead = Math.Min(_tail.Length, 48) + 4;
    }

    /// <summary>Takes a chunk of the continuation; returns the text that may be shown now (possibly empty).</summary>
    public string Push(string chunk)
    {
        if (_decided)
            return chunk;

        _pending.Append(chunk);
        return _pending.Length >= _lookahead ? Decide() : "";
    }

    /// <summary>Ends the stream: returns whatever is still held back.</summary>
    public string Flush() => _decided ? "" : Decide();

    private string Decide()
    {
        _decided = true;
        var text = _pending.ToString();
        _pending.Clear();

        var ellipsis = false;
        var lead = LeadingEllipsis().Match(text);
        if (lead.Success)
        {
            text = text[lead.Length..];
            ellipsis = true;
        }

        var best = Math.Max(Overlap(_tail, text), Overlap(_tail.TrimEnd(), text));
        if (best > 0)
            text = text[best..];

        if (ellipsis && text.Length > 0 && !_partialEndsWithSpace && !char.IsWhiteSpace(text[0]))
            text = " " + text;
        return text;
    }

    /// <summary>Length of the longest prefix of <paramref name="text"/> that ends <paramref name="tail"/>, on a word boundary.</summary>
    private static int Overlap(string tail, string text)
    {
        for (var k = Math.Min(tail.Length, text.Length); k >= MinOverlap; k--)
        {
            if (!tail.EndsWith(text.AsSpan(0, k), StringComparison.Ordinal))
                continue;

            var start = tail.Length - k;
            if (start == 0 || !char.IsLetterOrDigit(tail[start - 1]))
                return k;
        }

        return 0;
    }

    [GeneratedRegex(@"^\s*(?:\.{3,}|…)\s*")]
    private static partial Regex LeadingEllipsis();
}
