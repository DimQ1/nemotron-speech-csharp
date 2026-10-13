namespace VoiceType.Uno.Services;

/// <summary>"Reset settings to defaults": the options go back to their defaults; folders, models and history are not touched.</summary>
public static class SettingsDefaults
{
    /// <summary>Puts every option of <paramref name="settings"/> back to its default. Folders, the chosen models and History stay.</summary>
    /// <remarks>
    /// The Gemma compute (<see cref="AppSettings.TranslationComputeBackend"/>) stays too: it describes how the loaded language model
    /// runs and is switched with that model, not with the options.
    /// </remarks>
    public static void ResetOptions(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var d = new AppSettings();
        settings.Language = d.Language;
        settings.AudioSource = d.AudioSource;
        settings.ExecutionProvider = d.ExecutionProvider;
        settings.UseVad = d.UseVad;
        settings.RepetitionPenalty = d.RepetitionPenalty;
        settings.IsTextInjectionEnabled = d.IsTextInjectionEnabled;
        settings.IsAutoScrollEnabled = d.IsAutoScrollEnabled;
        settings.DisableInjectionOnFocusChange = d.DisableInjectionOnFocusChange;
        settings.AlwaysOnTop = d.AlwaysOnTop;
        settings.AutoStartRecognition = d.AutoStartRecognition;
        settings.ClearTextOnModelOrSessionChange = d.ClearTextOnModelOrSessionChange;
        settings.TranslationEnabled = d.TranslationEnabled;
        settings.TranslationTargetLanguage = d.TranslationTargetLanguage;
        settings.TranslationBackend = d.TranslationBackend;
        settings.TranslationServerUrl = d.TranslationServerUrl;
        settings.TranslationSystemPrompt = d.TranslationSystemPrompt;
        settings.PasteChord = d.PasteChord;
        settings.ToggleHotkey = d.ToggleHotkey;
        settings.MuteHotkey = d.MuteHotkey;
        settings.InjectTextHotkey = d.InjectTextHotkey;
    }

    /// <summary>A copy of <paramref name="current"/> with every option reset.</summary>
    public static AppSettings Reset(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var copy = current.Clone();
        ResetOptions(copy);
        return copy;
    }
}
