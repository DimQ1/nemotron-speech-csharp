using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>Audio input: Microphone / System audio / Microphone + system audio, with explanations (Android: microphone only).</summary>
internal sealed class AudioSection : SettingsSectionView
{
    public AudioSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var android = Ctx.Android;
        var current = SettingsText.NormalizeAudioSource(Ctx.Live.AudioSource, android);
        var radios = SettingsText.AudioSourceOptions(android)
            .Select(o => (UIElement)SettingsRows.Radio(this, "source", o, o.Value == current, value => Ctx.Change(s => s.AudioSource = value)))
            .ToArray();

        var stack = new StackPanel();
        stack.Children.Add(SettingsRows.SectionHeader("Source"));
        stack.Children.Add(SettingsRows.Card(radios));
        stack.Children.Add(SettingsRows.Note(android ? SettingsText.AudioCaptionAndroid : SettingsText.AudioCaptionDesktop));
        return stack;
    }
}

/// <summary>Typing into apps (desktop): switch, paste shortcut. "Stop when focus moves" is not shown: the Uno app does not read that setting.</summary>
internal sealed class TypingSection : SettingsSectionView
{
    public TypingSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var live = Ctx.Live;
        var stack = new StackPanel();

        var toggle = SettingsRows.Switch(live.IsTextInjectionEnabled, "Type into other apps", on => Ctx.Change(s => s.IsTextInjectionEnabled = on));
        stack.Children.Add(SettingsRows.Card(
            SettingsRows.Row(SettingsRows.Icon(Symbol.Keyboard), "Type into other apps", "Each finished phrase is pasted into the focused window", toggle)));

        stack.Children.Add(SettingsRows.SectionHeader("Paste shortcut"));
        var current = SettingsText.NormalizePasteChord(live.PasteChord);
        var radios = SettingsText.PasteChordOptionsFor(live.PasteChord)
            .Select(o => (UIElement)SettingsRows.Radio(this, "paste", o, string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase),
                value => Ctx.Change(s => s.PasteChord = value)))
            .ToArray();
        stack.Children.Add(SettingsRows.Card(radios));
        stack.Children.Add(SettingsRows.Note(SettingsText.TypingCaption));
        return stack;
    }
}

/// <summary>General: follow new text, auto-start, window on top (desktop), clear transcript, reset to defaults.</summary>
internal sealed class GeneralSection : SettingsSectionView
{
    public GeneralSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var live = Ctx.Live;
        var rows = new List<UIElement>
        {
            SettingsRows.Row(SettingsRows.Icon(Symbol.Sort), "Follow new text", "Keep the transcript scrolled to the end",
                SettingsRows.Switch(live.IsAutoScrollEnabled, "Follow new text", on => Ctx.Change(s => s.IsAutoScrollEnabled = on))),
            SettingsRows.Row(SettingsRows.Icon(Symbol.Play), "Start when the model is ready", "Recording begins right after launch",
                SettingsRows.Switch(live.AutoStartRecognition, "Start when the model is ready", on => Ctx.Change(s => s.AutoStartRecognition = on))),
        };
        if (!Ctx.Android)
        {
            rows.Add(SettingsRows.Row(SettingsRows.Icon(Symbol.Pin), "Keep window on top", "Stay above other windows",
                SettingsRows.Switch(live.AlwaysOnTop, "Keep window on top", on => Ctx.Change(s => s.AlwaysOnTop = on))));
        }
        rows.Add(SettingsRows.Row(SettingsRows.Icon(Symbol.Delete), "Clear transcript on model change", "Also when a new session starts",
            SettingsRows.Switch(live.ClearTextOnModelOrSessionChange, "Clear transcript on model change", on => Ctx.Change(s => s.ClearTextOnModelOrSessionChange = on))));

        var stack = new StackPanel();
        stack.Children.Add(SettingsRows.Card([.. rows]));
        stack.Children.Add(SettingsRows.SectionHeader("Reset"));
        stack.Children.Add(SettingsRows.Card(
            SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Refresh), "Reset settings to defaults", SettingsText.ResetSubtitle, () => _ = ResetAsync())));
        stack.Children.Add(SettingsRows.Note($"VoiceType {AppVersion()} · settings.json in App data (Settings › Storage)"));
        return stack;
    }

    private async Task ResetAsync()
    {
        var confirmed = await VoiceType.Uno.Presentation.History.HistoryDialogs.AskAsync(
            Ctx.XamlRoot, "Reset settings to defaults?",
            "Language, audio source, compute, translation, shortcuts and the other options go back to their defaults. Your folders, models and history are not touched.",
            "Reset");
        if (!confirmed)
            return;
        await Ctx.ChangeAsync(SettingsDefaults.ResetOptions);
        Content = Build();
    }

    private static string AppVersion()
    {
        var version = typeof(GeneralSection).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ApplicationDisplayVersion")?.Value;
        return string.IsNullOrWhiteSpace(version) ? "" : version;
    }
}
