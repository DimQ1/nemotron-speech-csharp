using System.IO;
using System.Text.RegularExpressions;
using SpeechLib.PostProcessing;
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

        // Use Chain of Responsibility for post-processing.
        // NOTE: WhitespaceNormalizer is NOT included here — it would collapse
        // spaces between words in real-time partial results, making text unreadable.
        // Whitespace normalization is only applied in ProcessFinal().
        var speechLibRules = rules
            .Select(r => new SpeechLib.PostProcessing.CompiledRule(r.Regex, r.Replacement))
            .ToList();

        var chain = new PostProcessingChain()
            .Add(new LanguageTagStripper())
            .Add(new RegexRuleProcessor(speechLibRules));

        return chain.Execute(raw);
    }

    public string Process(string raw, List<PostProcessingRule> rules, bool enabled)
    {
        return Process(raw, CompileRules(rules, enabled));
    }

    public string ProcessFinal(string raw, IReadOnlyList<CompiledRule> rules)
    {
        // Final pass: apply all rules AND normalize whitespace for clean output
        var speechLibRules = rules
            .Select(r => new SpeechLib.PostProcessing.CompiledRule(r.Regex, r.Replacement))
            .ToList();

        var chain = new PostProcessingChain()
            .Add(new LanguageTagStripper())
            .Add(new RegexRuleProcessor(speechLibRules))
            .Add(new WhitespaceNormalizer());

        return chain.Execute(raw).Trim();
    }

    [GeneratedRegex("""\s+""")]
    private static partial Regex WhitespaceRegex();
}