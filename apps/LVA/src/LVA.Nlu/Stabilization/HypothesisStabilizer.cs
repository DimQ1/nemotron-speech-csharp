namespace LVA.Nlu.Stabilization;

/// <summary>
/// Stabilizes partial ASR hypotheses before intent scoring (TZ §4.3):
/// longest common prefix across consecutive hypotheses; a token becomes
/// stable after N unchanged updates. L1 scoring runs on
/// "stable prefix + current tail"; prefix scores are cached by the caller.
/// </summary>
public sealed class HypothesisStabilizer
{
    private readonly int _stableAfter;
    private string _previous = string.Empty;
    private string _stablePrefix = string.Empty;
    private int _unchangedCount;

    public HypothesisStabilizer(int stableAfter = 3)
    {
        _stableAfter = stableAfter;
    }

    /// <summary>Current stable prefix (never shrinks within a segment).</summary>
    public string StablePrefix => _stablePrefix;

    /// <summary>
    /// Feeds a new partial hypothesis; returns the text that should be scored
    /// (stable prefix + current tail).
    /// </summary>
    public string Update(string hypothesis)
    {
        hypothesis = hypothesis.Trim();

        if (hypothesis == _previous)
        {
            _unchangedCount++;
        }
        else
        {
            _unchangedCount = 0;
            _previous = hypothesis;
        }

        // Longest common prefix between previous stable prefix and current hypothesis.
        var lcp = LongestCommonPrefix(_stablePrefix, hypothesis);
        if (_unchangedCount >= _stableAfter && lcp.Length > _stablePrefix.Length)
            _stablePrefix = lcp;

        return hypothesis;
    }

    /// <summary>True when the hypothesis has been unchanged for N updates.</summary>
    public bool IsStable => _unchangedCount >= _stableAfter;

    public void Reset()
    {
        _previous = string.Empty;
        _stablePrefix = string.Empty;
        _unchangedCount = 0;
    }

    public static string LongestCommonPrefix(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
            i++;
        return a[..i];
    }
}
