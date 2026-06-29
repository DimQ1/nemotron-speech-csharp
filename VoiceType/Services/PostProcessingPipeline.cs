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
    /// <summary>Apply all enabled rules to the raw transcription text.</summary>
    public static string Process(string raw, List<PostProcessingRule> rules, bool enabled)
    {
        if (!enabled || string.IsNullOrEmpty(raw)) return raw;

        var result = raw;
        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) continue;
            try
            {
                result = Regex.Replace(result, rule.Pattern, rule.Replacement,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch { /* skip malformed regex */ }
        }
        return result.Trim();
    }
}
