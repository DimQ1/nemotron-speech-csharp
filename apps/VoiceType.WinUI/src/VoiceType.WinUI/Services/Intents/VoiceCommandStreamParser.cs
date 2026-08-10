using System.Text.RegularExpressions;

namespace VoiceType.WinUI.Services.Intents;

/// <summary>Extracts the command-shaped part of an accumulated streaming ASR hypothesis.</summary>
public static partial class VoiceCommandStreamParser
{
    public static bool HasCommandLead(string text) => CommandLeadRegex().IsMatch(
        HeuristicScorer.NormalizeForMatching(text));

    public static string ExtractIntentCandidate(string text)
    {
        var normalized = HeuristicScorer.NormalizeForMatching(text).Trim();
        if (normalized.Length == 0)
            return normalized;

        var commandStarts = CommandLeadRegex().Matches(normalized);
        if (commandStarts.Count == 0)
            return normalized;

        // Streaming ASR returns an accumulated transcript. The last command is
        // the one the user just spoke and must override earlier state changes.
        var commandStart = commandStarts[^1];
        var candidate = normalized[commandStart.Index..];
        return candidate.Trim();
    }

    public static string ExtractTargetApplication(string text)
    {
        var candidate = ExtractIntentCandidate(text);
        var target = TargetRegex().Match(candidate);
        if (!target.Success)
            return "";

        return target.Groups["app"].Value
            .Trim()
            .TrimEnd('.', ',', '!', '?', ';', ':');
    }

    [GeneratedRegex(@"\b(?:давай\s+)?(?:начн(?:и|ем|емте)|включ(?:и|ить)|продолж(?:ай|ить)|останов(?:и|ить)|прекрати|выключ(?:и|ить)|перестань|набери|набрать|напечатай|напечатать|введи|ввести|отправь|продиктуй|запиши)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandLeadRegex();

    [GeneratedRegex(@"\b(?:в|на)\s+(?<app>[\p{L}\p{N}][\p{L}\p{N}\s+._-]{0,48})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetRegex();
}