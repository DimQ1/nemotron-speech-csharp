namespace SpeechLib.Translation;

/// <summary>A translation target: BCP-47-ish code plus the English name used in prompts.</summary>
public sealed record TranslationLanguage(string Code, string Name);

/// <summary>
/// Languages offered as translation targets, shared by every app so prompts always
/// carry a language <em>name</em> ("Russian"), not a code ("ru") the model may read as
/// a typo or an abbreviation of something else.
/// </summary>
public static class TranslationLanguages
{
    public static IReadOnlyList<TranslationLanguage> All { get; } =
    [
        new("ru", "Russian"),
        new("en", "English"),
        new("uk", "Ukrainian"),
        new("de", "German"),
        new("fr", "French"),
        new("es", "Spanish"),
        new("it", "Italian"),
        new("pt", "Portuguese"),
        new("pl", "Polish"),
        new("nl", "Dutch"),
        new("zh", "Chinese"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("tr", "Turkish"),
        new("ar", "Arabic"),
        new("hi", "Hindi"),
    ];

    /// <summary>Codes that name a real language ("auto" and blanks are not translation targets).</summary>
    public static bool IsTargetCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && !string.Equals(code.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The prompt-ready name for a code or name. Unknown values are returned as given
    /// (a user may type "Belarusian"); "auto", blanks and null yield null.
    /// </summary>
    public static string? NameFor(string? codeOrName)
    {
        if (!IsTargetCode(codeOrName))
            return null;

        var value = codeOrName!.Trim();
        var known = All.FirstOrDefault(l =>
            string.Equals(l.Code, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(l.Name, value, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
            return known.Name;

        // ASR languages are often stored as region tags ("en-US", "ru-RU").
        var dash = value.IndexOf('-');
        if (dash > 0)
        {
            var primary = value[..dash];
            var byPrimary = All.FirstOrDefault(l => string.Equals(l.Code, primary, StringComparison.OrdinalIgnoreCase));
            if (byPrimary is not null)
                return byPrimary.Name;
        }

        return value;
    }
}
