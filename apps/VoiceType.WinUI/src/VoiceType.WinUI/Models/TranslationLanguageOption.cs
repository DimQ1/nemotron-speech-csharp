namespace VoiceType.WinUI.Models;

/// <summary>
/// A target language offered by the live-translation ComboBox.
/// <paramref name="Code"/> is the BCP-47 tag persisted to settings;
/// <paramref name="Name"/> is the natural-language name embedded in the
/// translation prompt (e.g. "Russian"), which Gemma 4 understands reliably.
/// </summary>
public sealed record TranslationLanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}
