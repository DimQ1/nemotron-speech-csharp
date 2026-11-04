using VoiceType.Uno.Localization;
using VoiceType.Uno.Presentation.Localization;

namespace VoiceType.Uno.Presentation;

/// <summary>The interface language picker of the main menu (Language section).</summary>
public sealed partial class MainViewModel
{
    /// <summary>The interface languages, each in its own name.</summary>
    public IReadOnlyList<UiLanguage> UiLanguages => Loc.Languages;

    /// <summary>The current interface language; setting it stores the choice and switches the app at once.</summary>
    public UiLanguage SelectedUiLanguage
    {
        get => Loc.Current;
        set
        {
            if (value is null || value == Loc.Current)
                return;
            LanguagePrompt.Apply(_settingsStore, value.Code);
        }
    }
}
