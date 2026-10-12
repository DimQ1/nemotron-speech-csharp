namespace VoiceType.Uno.Services;

/// <summary>
/// Builds the one-line summary of the active modes shown under the app title and in the
/// recording notification: "Parakeet TDT 0.6B v3 · Auto · → RU · Inject".
/// </summary>
public static class ModeSummaryFormatter
{
    public const string Separator = " · ";

    /// <param name="modelDisplayName">Friendly model name; blank falls back to "No model".</param>
    /// <param name="recognitionLanguage">Language code ("auto", "en", …).</param>
    /// <param name="translationTarget">Target language code, or <c>null</c> when translation is off.</param>
    /// <param name="isTextInjectionEnabled">Appends "Inject" when each phrase is typed into the focused app.</param>
    public static string Format(
        string? modelDisplayName,
        string? recognitionLanguage,
        string? translationTarget,
        bool isTextInjectionEnabled)
    {
        var parts = new List<string>(4)
        {
            string.IsNullOrWhiteSpace(modelDisplayName) ? "No model" : modelDisplayName.Trim(),
            FormatLanguage(recognitionLanguage)
        };

        if (!string.IsNullOrWhiteSpace(translationTarget))
            parts.Add("→ " + translationTarget.Trim().ToUpperInvariant());
        if (isTextInjectionEnabled)
            parts.Add("Inject");

        return string.Join(Separator, parts);
    }

    /// <summary>"auto" (or blank) reads as "Auto"; a language code is shown upper-case ("EN").</summary>
    public static string FormatLanguage(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode) || string.Equals(languageCode.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? "Auto"
            : languageCode.Trim().ToUpperInvariant();
}
