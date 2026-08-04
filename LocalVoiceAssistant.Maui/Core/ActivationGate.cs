namespace LocalVoiceAssistant.Maui.Core;

public interface IActivationGate
{
    ActivationDecision Evaluate(string text);
}

public sealed class ActivationGate : IActivationGate
{
    private static readonly string[] Markers =
    [
        "ассистент",
        "помощник",
        "эй lva",
        "hey lva",
        "lva"
    ];

    private static readonly string[] IntentTerms =
    [
        "найди",
        "поиск",
        "интернет",
        "открой",
        "скажи",
        "покажи",
        "время",
        "дата",
        "помощь",
        "стоп",
        "хватит"
    ];

    public ActivationDecision Evaluate(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
            return new(false, 0, false, string.Empty, null);

        var marker = Markers.FirstOrDefault(candidate =>
            normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        var hasMarker = marker is not null;
        var hasIntent = IntentTerms.Any(term =>
            normalized.Contains(term, StringComparison.OrdinalIgnoreCase));

        var semanticScore = (hasMarker, hasIntent) switch
        {
            (true, true) => 0.97,
            (true, false) => 0.86,
            _ => 0.20
        };

        var cleanText = hasMarker
            ? normalized.Replace(marker!, string.Empty, StringComparison.OrdinalIgnoreCase).Trim(' ', ',', '.', '!', '?')
            : normalized;

        return new(
            hasMarker && hasIntent && semanticScore >= 0.85,
            semanticScore,
            hasMarker,
            cleanText,
            marker);
    }
}
