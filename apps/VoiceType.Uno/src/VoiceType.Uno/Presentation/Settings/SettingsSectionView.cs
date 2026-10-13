using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>One section of the Settings page. Built from the live settings when shown; edits apply at once.</summary>
internal abstract class SettingsSectionView : UserControl
{
    protected SettingsSectionView(SettingsContext context)
    {
        Ctx = context;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    protected SettingsContext Ctx { get; }

    /// <summary>Builds the content. Called once, from <see cref="Create"/>.</summary>
    protected abstract UIElement Build();

    /// <summary>The section is on screen: subscribe to what it follows, measure what needs measuring.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>The section left the screen: drop subscriptions.</summary>
    public virtual void OnHidden()
    {
    }

    public static SettingsSectionView Create(SettingsSection section, SettingsContext context)
    {
        SettingsSectionView view = section switch
        {
            SettingsSection.Storage => new StorageSection(context),
            SettingsSection.Speech => new SpeechSection(context),
            SettingsSection.Audio => new AudioSection(context),
            SettingsSection.Translation => new TranslationSection(context),
            SettingsSection.Typing => new TypingSection(context),
            SettingsSection.Hotkeys => new HotkeysSection(context),
            _ => new GeneralSection(context),
        };
        view.Content = view.Build();
        return view;
    }
}
