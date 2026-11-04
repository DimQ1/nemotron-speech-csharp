using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>Translation and Gemma: live translation switch and target, engine, Gemma model and compute or server address, extra instructions.</summary>
internal sealed class TranslationSection : SettingsSectionView
{
    private static string OtherLanguage => Loc.T("Settings.Translation.Other");

    private StackPanel? _engineHost;
    private StackPanel? _targetHost;
    private TextBlock? _urlError;

    public TranslationSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var live = Ctx.Live;
        var stack = new StackPanel();

        // ── Live translation ──────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader(Loc.T("Settings.Translation.LiveHeader")));
        var toggle = SettingsRows.Switch(live.TranslationEnabled, Loc.T("Settings.Translation.Toggle.Title"), on => Ctx.Change(s => s.TranslationEnabled = on));
        var toggleRow = SettingsRows.Row(SettingsRows.Icon(Symbol.Globe), Loc.T("Settings.Translation.Toggle.Title"), Loc.T("Settings.Translation.Toggle.Detail"), toggle);
        _targetHost = new StackPanel();
        stack.Children.Add(SettingsRows.Card(toggleRow, _targetHost));
        BuildTarget();

        // ── Engine ────────────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader(Loc.T("Settings.Translation.EngineHeader")));
        var engine = SettingsText.NormalizeEngine(live.TranslationBackend);
        var radios = SettingsText.EngineOptions
            .Select(o => (UIElement)SettingsRows.Radio(this, "engine", o, o.Value == engine, value =>
            {
                Ctx.Change(s => s.TranslationBackend = value);
                BuildEngine(value);
            }))
            .ToArray();
        stack.Children.Add(SettingsRows.Card(radios));
        _engineHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(_engineHost);
        BuildEngine(engine);

        // ── Instructions ──────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader(Loc.T("Settings.Translation.InstructionsHeader")));
        var prompt = new TextBox
        {
            Text = live.TranslationSystemPrompt ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 88,
            MaxHeight = 200,
            PlaceholderText = Loc.T("Settings.Translation.Instructions.Placeholder"),
        };
        AutomationProperties.SetName(prompt, Loc.T("Settings.Translation.Instructions.Title"));
        prompt.LostFocus += (_, _) =>
        {
            var text = prompt.Text.Trim();
            if (text != (Ctx.Live.TranslationSystemPrompt ?? "").Trim())
                Ctx.Change(s => s.TranslationSystemPrompt = text);
        };
        stack.Children.Add(SettingsRows.Card(
            SettingsRows.StackRow(SettingsRows.Icon(Symbol.Document), Loc.T("Settings.Translation.Instructions.Title"),
                Loc.T("Settings.Translation.Instructions.Detail"), prompt, out _)));
        return stack;
    }

    /// <summary>"Translate into": the shared language names, plus "Other…" for a typed code or name.</summary>
    private void BuildTarget()
    {
        if (_targetHost is null)
            return;
        _targetHost.Children.Clear();
        _targetHost.Children.Add(SettingsRows.Divider());

        var stored = Ctx.Live.TranslationTargetLanguage;
        var custom = SettingsText.IsCustomTarget(stored);
        var names = SettingsText.TranslationTargets.Select(t => t.Title).Append(OtherLanguage).ToList();
        var index = custom
            ? names.Count - 1
            : Math.Max(0, SettingsText.TranslationTargets.ToList().FindIndex(t =>
                string.Equals(t.Value, stored?.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(t.Title, stored?.Trim(), StringComparison.OrdinalIgnoreCase)));

        var customBox = SettingsRows.MonoBox(custom ? stored! : "", Loc.T("Settings.Translation.Target.CustomName"), Loc.T("Settings.Translation.Target.CustomPlaceholder"));
        customBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        customBox.Margin = new Thickness(12, 0, 12, 8);

        var combo = SettingsRows.Combo(names, index, Loc.T("Settings.Translation.Target.Title"), i =>
        {
            if (i == names.Count - 1)
            {
                customBox.Visibility = Visibility.Visible;
                customBox.Focus(FocusState.Programmatic);
                return;
            }
            customBox.Visibility = Visibility.Collapsed;
            var code = SettingsText.TranslationTargets[i].Value;
            Ctx.Change(s => s.TranslationTargetLanguage = code);
        }, minWidth: 140);
        _targetHost.Children.Add(SettingsRows.Row(SettingsRows.Icon(Symbol.World), Loc.T("Settings.Translation.Target.Title"), Loc.T("Settings.Translation.Target.Detail"), combo));

        customBox.LostFocus += (_, _) =>
        {
            var text = customBox.Text.Trim();
            if (text.Length > 0 && !string.Equals(text, Ctx.Live.TranslationTargetLanguage, StringComparison.Ordinal))
                Ctx.Change(s => s.TranslationTargetLanguage = text);
        };
        _targetHost.Children.Add(customBox);
    }

    /// <summary>On this device: the Gemma model and its compute. Local server: the address.</summary>
    private void BuildEngine(string engine)
    {
        if (_engineHost is null)
            return;
        _engineHost.Children.Clear();

        if (engine == SettingsText.EngineHttp)
        {
            var url = SettingsRows.MonoBox(Ctx.Live.TranslationServerUrl, Loc.T("Settings.Translation.Server.Title"), "http://localhost:9379");
            _urlError = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed, Text = SettingsText.ServerUrlError };
            _urlError.Foreground = SettingsRows.ThemeBrush(this, "HistoryDangerBrush");
            var commit = () =>
            {
                var text = url.Text.Trim();
                if (!SettingsText.IsValidServerUrl(text))
                {
                    _urlError.Visibility = Visibility.Visible;
                    return;
                }
                _urlError.Visibility = Visibility.Collapsed;
                if (!string.Equals(text, Ctx.Live.TranslationServerUrl, StringComparison.Ordinal))
                    Ctx.Change(s => s.TranslationServerUrl = text);
            };
            url.LostFocus += (_, _) => commit();
            url.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                    commit();
            };
            var holder = SettingsRows.Column(url, _urlError);
            _engineHost.Children.Add(SettingsRows.Card(
                SettingsRows.StackRow(SettingsRows.Icon(Symbol.World), Loc.T("Settings.Translation.Server.Title"), SettingsText.ServerUrlHint, holder, out _)));
            return;
        }

        var llm = App.Services.GetRequiredService<ILlmModelService>();
        var model = llm.Active;
        var state = llm.StateOf(model);
        var installed = state is LlmModelState.Installed or LlmModelState.Active;
        var detail = HistoryUiFormat.Join(
            installed ? Loc.T("Settings.Translation.Model.Installed") : state == LlmModelState.Downloading ? Loc.T("Settings.Translation.Model.Downloading") : Loc.T("Settings.Translation.Model.NotDownloaded"),
            Loc.T("Settings.Translation.Model.Shared"),
            Loc.F("Settings.Speech.Model.Folder", ModelStorageLayout.LlmFolderName),
            HistoryUiFormat.Bytes(model.SizeBytes));
        var link = SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Library), model.Title, detail, () => Ctx.Navigate(typeof(ModelsPage), ModelsTab.Translation), Loc.T("Settings.Translation.Model.Name"));

        var compute = SettingsRows.Segmented(this, SettingsText.GemmaComputeOptions, SettingsText.NormalizeGemmaCompute(Ctx.Live.TranslationComputeBackend), "gemma-compute",
            value => _ = SwitchGemmaComputeAsync(llm, value));
        var computeRow = SettingsRows.StackRow(SettingsRows.Icon(Symbol.Setting), Loc.T("Settings.Translation.Compute.Title"),
            Loc.T("Settings.Translation.Compute.Detail"), compute, out _);
        _engineHost.Children.Add(SettingsRows.Card(link, computeRow));
    }

    private async Task SwitchGemmaComputeAsync(ILlmModelService llm, string backend)
    {
        try
        {
            // The language-model service reloads the shared engine and persists the choice through the settings store.
            await llm.SetComputeBackendAsync(backend);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Gemma compute could not be switched: {ex.Message}");
        }
    }
}
