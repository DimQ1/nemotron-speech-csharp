using SpeechLib.Translation;

namespace VoiceType.Uno.Localization;

/// <summary>
/// Language names for the interface (recognition languages, translation targets) in the current interface language:
/// <c>Languages.&lt;code&gt;</c> keys, else the English name of <see cref="TranslationLanguages"/>, else the code.
/// Prompts to the language model keep using the English <see cref="TranslationLanguages.NameFor"/>.
/// </summary>
public static class LanguageNames
{
    public static string? Of(string? codeOrName)
    {
        if (string.IsNullOrWhiteSpace(codeOrName))
            return null;
        var english = TranslationLanguages.NameFor(codeOrName);
        var code = TranslationLanguages.All.FirstOrDefault(l =>
            string.Equals(l.Code, codeOrName.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(l.Name, codeOrName.Trim(), StringComparison.OrdinalIgnoreCase))?.Code
            ?? codeOrName.Trim().Split('-')[0].ToLowerInvariant();
        var key = "Languages." + code;
        var text = Loc.T(key);
        return text != key ? text : english;
    }
}
