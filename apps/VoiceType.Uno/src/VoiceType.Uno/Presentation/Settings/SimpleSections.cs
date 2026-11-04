using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.Services;
using VoiceType.Uno.Localization;

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
        stack.Children.Add(SettingsRows.SectionHeader(Loc.T("Settings.Audio.SourceHeader")));
        stack.Children.Add(SettingsRows.Card(radios));
        stack.Children.Add(SettingsRows.Note(android ? SettingsText.AudioCaptionAndroid : SettingsText.AudioCaptionDesktop));
        return stack;
    }
}

/// <summary>
/// Typing into apps (desktop): switch, how the phrase is inserted (typed or pasted), and the paste shortcut,
/// which only matters in paste mode. "Stop when focus moves" is not shown: the Uno app does not read that setting.
/// </summary>
internal sealed class TypingSection : SettingsSectionView
{
    private StackPanel? _pasteHost;

    public TypingSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var live = Ctx.Live;
        var stack = new StackPanel();

        var toggle = SettingsRows.Switch(live.IsTextInjectionEnabled, Loc.T("Settings.Typing.Toggle.Title"), on => Ctx.Change(s => s.IsTextInjectionEnabled = on));
        stack.Children.Add(SettingsRows.Card(
            SettingsRows.Row(SettingsRows.Icon(Symbol.Keyboard), Loc.T("Settings.Typing.Toggle.Title"), Loc.T("Settings.Typing.Toggle.Detail"), toggle)));

        stack.Children.Add(SettingsRows.SectionHeader(Loc.T("Settings.Typing.MethodHeader")));
        var method = SettingsText.NormalizeInjectionMethod(live.TextInjectionMethod);
        var methods = SettingsText.InjectionMethodOptions
            .Select(o => (UIElement)SettingsRows.Radio(this, "method", o, o.Value == method, value =>
            {
                Ctx.Change(s => s.TextInjectionMethod = value);
                BuildPaste(value);
            }))
            .ToArray();
        stack.Children.Add(SettingsRows.Card(methods));

        _pasteHost = new StackPanel();
        stack.Children.Add(_pasteHost);
        BuildPaste(method);
        return stack;
    }

    /// <summary>The paste shortcut (only in paste mode) and the note for the chosen method.</summary>
    private void BuildPaste(string method)
    {
        if (_pasteHost is null)
            return;
        _pasteHost.Children.Clear();

        if (SettingsText.NormalizeInjectionMethod(method) == SettingsText.InjectionPaste)
        {
            _pasteHost.Children.Add(SettingsRows.SectionHeader(Loc.T("Settings.Typing.ChordHeader")));
            var current = SettingsText.NormalizePasteChord(Ctx.Live.PasteChord);
            var radios = SettingsText.PasteChordOptionsFor(Ctx.Live.PasteChord)
                .Select(o => (UIElement)SettingsRows.Radio(this, "paste", o, string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase),
                    value => Ctx.Change(s => s.PasteChord = value)))
                .ToArray();
            _pasteHost.Children.Add(SettingsRows.Card(radios));
        }
        _pasteHost.Children.Add(SettingsRows.Note(SettingsText.TypingCaptionFor(method)));
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
            SettingsRows.Row(SettingsRows.Icon(Symbol.Sort), Loc.T("Settings.General.Follow.Title"), Loc.T("Settings.General.Follow.Detail"),
                SettingsRows.Switch(live.IsAutoScrollEnabled, Loc.T("Settings.General.Follow.Title"), on => Ctx.Change(s => s.IsAutoScrollEnabled = on))),
            SettingsRows.Row(SettingsRows.Icon(Symbol.Play), Loc.T("Settings.General.AutoStart.Title"), Loc.T("Settings.General.AutoStart.Detail"),
                SettingsRows.Switch(live.AutoStartRecognition, Loc.T("Settings.General.AutoStart.Title"), on => Ctx.Change(s => s.AutoStartRecognition = on))),
        };
        if (!Ctx.Android)
        {
            rows.Add(SettingsRows.Row(SettingsRows.Icon(Symbol.Pin), Loc.T("Settings.General.OnTop.Title"), Loc.T("Settings.General.OnTop.Detail"),
                SettingsRows.Switch(live.AlwaysOnTop, Loc.T("Settings.General.OnTop.Title"), on => Ctx.Change(s => s.AlwaysOnTop = on))));
        }
        rows.Add(SettingsRows.Row(SettingsRows.Icon(Symbol.Delete), Loc.T("Settings.General.Clear.Title"), Loc.T("Settings.General.Clear.Detail"),
            SettingsRows.Switch(live.ClearTextOnModelOrSessionChange, Loc.T("Settings.General.Clear.Title"), on => Ctx.Change(s => s.ClearTextOnModelOrSessionChange = on))));

        var stack = new StackPanel();
        stack.Children.Add(SettingsRows.Card([.. rows]));
        if (VoiceType.Uno.Services.UpdateService.IsSupported)
            AddUpdates(stack, live);
        stack.Children.Add(SettingsRows.SectionHeader(Loc.T("Settings.General.ResetHeader")));
        stack.Children.Add(SettingsRows.Card(
            SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Refresh), Loc.T("Settings.General.Reset.Title"), SettingsText.ResetSubtitle, () => _ = ResetAsync())));
        stack.Children.Add(SettingsRows.Note(Loc.F("Settings.General.VersionNote", AppVersion())));
        return stack;
    }

    /// <summary>Updates from GitHub Releases: check at start, check now (with what the last check found).</summary>
    private void AddUpdates(StackPanel stack, AppSettings live)
    {
        var updates = App.Services.GetRequiredService<VoiceType.Uno.Services.UpdateService>();
        stack.Children.Add(SettingsRows.SectionHeader(Loc.T("Updates.Settings.Header")));
        var auto = SettingsRows.Row(SettingsRows.Icon(Symbol.Sync), Loc.T("Updates.Settings.Auto.Title"), Loc.T("Updates.Settings.Auto.Detail"),
            SettingsRows.Switch(live.CheckUpdatesOnStart, Loc.T("Updates.Settings.Auto.Title"), on => Ctx.Change(s => s.CheckUpdatesOnStart = on)));
        TextBlock? detail = null;
        var check = SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Download), Loc.T("Updates.Settings.Check.Title"), updates.StatusText,
            () => _ = CheckNowAsync(updates), out detail);
        stack.Children.Add(SettingsRows.Card(auto, check));

        void OnChanged() => DispatcherQueue.TryEnqueue(() => detail.Text = updates.StatusText);
        updates.Changed += OnChanged;
        Unloaded += (_, _) => updates.Changed -= OnChanged;
    }

    private async Task CheckNowAsync(VoiceType.Uno.Services.UpdateService updates)
    {
        var plan = await updates.CheckAsync();
        if (plan is not null && Ctx.XamlRoot is { } root)
            await UpdateDialog.ShowAsync(root, updates, plan,
                () => App.Services.GetRequiredService<MainViewModel>().IsRecording);
    }

    private async Task ResetAsync()
    {
        var confirmed = await VoiceType.Uno.Presentation.History.HistoryDialogs.AskAsync(
            Ctx.XamlRoot, Loc.T("Settings.General.ResetDialog.Title"),
            Loc.T("Settings.General.ResetDialog.Body"),
            Loc.T("Settings.General.ResetDialog.Confirm"));
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
