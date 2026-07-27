using System.Text;
using System.Text.RegularExpressions;

namespace SpeechLib.PostProcessing;

/// <summary>
/// Strips language tags like &lt;en-US&gt;, &lt;bg-BG&gt;, &lt;auto&gt; from text.
/// Early exit if text becomes empty after stripping.
/// </summary>
public sealed partial class LanguageTagStripper : PostProcessorBase
{
    [GeneratedRegex(@"<\w{2,3}(-\w{2,4})?>", RegexOptions.Compiled)]
    private static partial Regex LanguageTagPattern();

    protected override string? ProcessCore(string text)
    {
        var result = LanguageTagPattern().Replace(text, "").Trim();
        return result.Length == 0 ? null : result;
    }
}

/// <summary>
/// Applies a list of regex-based find-and-replace rules.
/// </summary>
public sealed class RegexRuleProcessor : PostProcessorBase
{
    private readonly IReadOnlyList<CompiledRule> _rules;

    public RegexRuleProcessor(IReadOnlyList<CompiledRule> rules) => _rules = rules;

    protected override string? ProcessCore(string text)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(text))
            return text;

        var result = text;
        foreach (var rule in _rules)
            result = rule.Regex.Replace(result, rule.Replacement);

        return string.IsNullOrEmpty(result) ? null : result;
    }
}

/// <summary>
/// Normalizes whitespace: collapses multiple spaces, trims.
/// </summary>
public sealed partial class WhitespaceNormalizer : PostProcessorBase
{
    [GeneratedRegex("""\s+""", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    protected override string? ProcessCore(string text)
    {
        var result = WhitespacePattern().Replace(text, " ").Trim();
        return result.Length == 0 ? null : result;
    }
}

/// <summary>
/// A compiled regex rule for post-processing.
/// </summary>
public sealed record CompiledRule(Regex Regex, string Replacement);
