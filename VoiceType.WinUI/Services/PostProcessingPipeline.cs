using System.IO;
using System.Text.RegularExpressions;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

public sealed partial class PostProcessingPipeline : IPostProcessingPipeline
{
    public sealed record CompiledRule(Regex Regex, string Replacement);

    public IReadOnlyList<CompiledRule> CompileRules(List<PostProcessingRule> rules, bool enabled)
    {
        if (!enabled || rules.Count == 0)
            return Array.Empty<CompiledRule>();

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
            catch { }
        }

        return compiled;
    }

    public string Process(string raw, IReadOnlyList<CompiledRule> rules)
    {
        if (rules.Count == 0 || string.IsNullOrEmpty(raw))
            return raw;

        var result = raw;
        foreach (var rule in rules)
            result = rule.Regex.Replace(result, rule.Replacement);

        result = WhitespaceRegex().Replace(result, " ");
        return result;
    }

    public string Process(string raw, List<PostProcessingRule> rules, bool enabled)
    {
        return Process(raw, CompileRules(rules, enabled));
    }

    public string ProcessFinal(string raw, IReadOnlyList<CompiledRule> rules)
    {
        return Process(raw, rules).Trim();
    }

    [GeneratedRegex("""\s+""")]
    private static partial Regex WhitespaceRegex();
}