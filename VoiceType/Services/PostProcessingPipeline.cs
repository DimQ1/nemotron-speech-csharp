using System.IO;
using System.Text.RegularExpressions;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Applies a configured pipeline of text post-processing rules.
/// Removes artifacts, normalises whitespace, and cleans recognition output.
/// </summary>
public static class PostProcessingPipeline
{
    public sealed record CompiledRule(Regex Regex, string Replacement);

    public static IReadOnlyList<CompiledRule> CompileRules(List<PostProcessingRule> rules, bool enabled)
    {
        if (!enabled || rules.Count == 0)
            return [];

        var compiled = new List<CompiledRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern))
                continue;

            try
            {
                compiled.Add(new CompiledRule(
                    new Regex(rule.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                    rule.Replacement));
            }
            catch { /* skip malformed regex */ }
        }

        return compiled;
    }

    public static string Process(string raw, IReadOnlyList<CompiledRule> rules)
    {
        if (rules.Count == 0 || string.IsNullOrEmpty(raw))
            return raw;

        var result = raw;
        foreach (var rule in rules)
            result = rule.Regex.Replace(result, rule.Replacement);

        return result.Trim();
    }

    /// <summary>Apply all enabled rules to the raw transcription text.</summary>
    public static string Process(string raw, List<PostProcessingRule> rules, bool enabled)
    {
        return Process(raw, CompileRules(rules, enabled));
    }
}
