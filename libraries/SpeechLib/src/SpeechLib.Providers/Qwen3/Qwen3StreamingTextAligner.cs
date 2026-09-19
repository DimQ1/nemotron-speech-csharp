namespace SpeechLib.Qwen3;

/// <summary>
/// Commits text from a bounded sliding audio window when the next window
/// confirms the overlapping suffix. It keeps a small fallback holdback when
/// model wording changes at the window boundary.
/// </summary>
internal sealed class Qwen3StreamingTextAligner
{
    private const int FallbackHoldWords = 2;
    private readonly bool _emitFirstBlock;
    private string[]? _pendingWords;
    private string[]? _growingSnapshot;
    private int _committedGrowingWords;
    private bool _hasShiftedWindow;
    private bool _emittedText;

    public bool HasPendingText =>
        _pendingWords is { Length: > 0 }
        || (_growingSnapshot is { Length: > 0 } && _committedGrowingWords < _growingSnapshot.Length);

    /// <param name="emitFirstBlock">
    /// When true the first decoded block is emitted immediately (minus the
    /// holdback), which makes text appear at the first block boundary (~1 s) on
    /// short utterances. Measured cost on CV17: English WER 9.74% to 13.23%,
    /// Russian 10.53% to 14.83%, because the append-only transcript contract
    /// cannot retract words the first, part-window hypothesis got wrong. Off by
    /// default; the accurate policy waits for a second decode to agree.
    /// </param>
    public Qwen3StreamingTextAligner(bool emitFirstBlock = false)
    {
        _emitFirstBlock = emitFirstBlock;
    }

    /// <summary>
    /// Current, still-uncommitted text of the latest decode. It is revisable: a
    /// caller should display it as a running partial and replace it on the next
    /// decode, never append it. Empty when everything decoded so far is committed.
    /// </summary>
    public string PartialText
    {
        get
        {
            if (_pendingWords is { Length: > 0 } pending)
                return string.Join(" ", pending);

            if (_growingSnapshot is { Length: > 0 } growing && _committedGrowingWords < growing.Length)
                return string.Join(" ", growing.Skip(_committedGrowingWords));

            return "";
        }
    }

    public string? Push(string text, bool windowShifted = true)
    {
        var currentWords = Tokenize(text);
        if (!_hasShiftedWindow && !windowShifted)
            return PushGrowing(currentWords);

        if (!_hasShiftedWindow)
        {
            var growingWords = _growingSnapshot ?? Array.Empty<string>();
            int committed = Math.Min(_committedGrowingWords, growingWords.Length);
            _pendingWords = growingWords[committed..];
            _growingSnapshot = null;
            _hasShiftedWindow = true;
        }

        return PushSliding(currentWords);
    }

    public string? Flush()
    {
        if (_growingSnapshot is not null)
        {
            int committed = Math.Min(_committedGrowingWords, _growingSnapshot.Length);
            var growingDelta = FormatDelta(_growingSnapshot, committed, _growingSnapshot.Length);
            _growingSnapshot = null;
            _committedGrowingWords = 0;
            return growingDelta;
        }

        if (_pendingWords is null)
            return null;

        var delta = FormatDelta(_pendingWords, 0, _pendingWords.Length);
        _pendingWords = null;
        return delta;
    }

    public void Reset()
    {
        _pendingWords = null;
        _growingSnapshot = null;
        _committedGrowingWords = 0;
        _hasShiftedWindow = false;
        _emittedText = false;
    }

    private string? PushGrowing(string[] currentWords)
    {
        if (_growingSnapshot is null)
        {
            _growingSnapshot = currentWords;
            if (!_emitFirstBlock)
                return null;

            // Emit the first block's hypothesis right away (minus the holdback)
            // instead of waiting for a second decode to agree with it: on a
            // 6-second clip that moved the first visible text from 5.65 s to
            // 1.04 s, at the cost of committing part-window words.
            _committedGrowingWords = Math.Max(0, currentWords.Length - FallbackHoldWords);
            return FormatDelta(currentWords, 0, _committedGrowingWords);
        }

        int commonPrefix = FindCommonPrefix(_growingSnapshot, currentWords);
        int stableCount = Math.Max(_committedGrowingWords, commonPrefix - FallbackHoldWords);
        stableCount = Math.Min(stableCount, currentWords.Length);
        var delta = FormatDelta(currentWords, _committedGrowingWords, stableCount);
        _committedGrowingWords = Math.Max(_committedGrowingWords, stableCount);
        _growingSnapshot = currentWords;
        return delta;
    }

    private string? PushSliding(string[] currentWords)
    {
        if (_pendingWords is null)
        {
            _pendingWords = currentWords;
            return null;
        }

        int overlap = FindSuffixPrefixOverlap(_pendingWords, currentWords);
        int commitCount = overlap > 0
            ? _pendingWords.Length - overlap
            : Math.Max(0, _pendingWords.Length - FallbackHoldWords);

        var delta = FormatDelta(_pendingWords, 0, commitCount);
        _pendingWords = currentWords;
        return delta;
    }

    private string? FormatDelta(IReadOnlyList<string> words, int start, int count)
    {
        if (count <= start)
            return null;

        var text = string.Join(" ", words.Skip(start).Take(count - start));
        if (_emittedText)
            text = " " + text;

        _emittedText = true;
        return text;
    }

    private static int FindCommonPrefix(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        int length = Math.Min(left.Count, right.Count);
        int common = 0;
        while (common < length && WordsEqual(left[common], right[common]))
            common++;
        return common;
    }

    private static int FindSuffixPrefixOverlap(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        int max = Math.Min(previous.Count, current.Count);
        for (int length = max; length >= 1; length--)
        {
            if (length == 1 && max > 1)
                continue;

            bool matches = true;
            int previousStart = previous.Count - length;
            for (int i = 0; i < length; i++)
            {
                if (!WordsEqual(previous[previousStart + i], current[i]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return length;
        }

        return 0;
    }

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool WordsEqual(string left, string right) =>
        NormalizeWord(left).Equals(NormalizeWord(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWord(string word)
    {
        var normalized = new string(word.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length == 0 ? word : normalized;
    }
}