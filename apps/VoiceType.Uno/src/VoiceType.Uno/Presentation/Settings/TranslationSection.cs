using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>Translation and Gemma: live translation switch and target, engine, Gemma model and compute or server address, extra instructions.</summary>
internal sealed class TranslationSection : SettingsSectionView
{
    private const string OtherLanguage = "Other…";

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
        stack.Children.Add(SettingsRows.SectionHeader("Live translation"));
        var toggle = SettingsRows.Switch(live.TranslationEnabled, "Translate while recording", on => Ctx.Change(s => s.TranslationEnabled = on));
        var toggleRow = SettingsRows.Row(SettingsRows.Icon(Symbol.Globe), "Translate while recording", "Also in the menu as a mode", toggle);
        _targetHost = new StackPanel();
        stack.Children.Add(SettingsRows.Card(toggleRow, _targetHost));
        BuildTarget();

        // ── Engine ────────────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Engine"));
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
        stack.Children.Add(SettingsRows.SectionHeader("Instructions"));
        var prompt = new TextBox
        {
            Text = live.TranslationSystemPrompt ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 88,
            MaxHeight = 200,
            PlaceholderText = "For example: keep product names in English.",
        };
        AutomationProperties.SetName(prompt, "Extra instructions for Gemma");
        prompt.LostFocus += (_, _) =>
        {
            var text = prompt.Text.Trim();
            if (text != (Ctx.Live.TranslationSystemPrompt ?? "").Trim())
                Ctx.Change(s => s.TranslationSystemPrompt = text);
        };
        stack.Children.Add(SettingsRows.Card(
            SettingsRows.StackRow(SettingsRows.Icon(Symbol.Document), "Extra instructions for Gemma",
                "Added to the built-in prompt: terms, style, casing", prompt, out _)));
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

        var customBox = SettingsRows.MonoBox(custom ? stored! : "", "Translate into: language code or name", "ru, de or Belarusian");
        customBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        customBox.Margin = new Thickness(12, 0, 12, 8);

        var combo = SettingsRows.Combo(names, index, "Translate into", i =>
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
        _targetHost.Children.Add(SettingsRows.Row(SettingsRows.Icon(Symbol.World), "Translate into", "A code (ru, de) or a name (Belarusian)", combo));

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
            var url = SettingsRows.MonoBox(Ctx.Live.TranslationServerUrl, "Server address", "http://localhost:9379");
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
                SettingsRows.StackRow(SettingsRows.Icon(Symbol.World), "Server address", SettingsText.ServerUrlHint, holder, out _)));
            return;
        }

        var llm = App.Services.GetRequiredService<ILlmModelService>();
        var model = llm.Active;
        var state = llm.StateOf(model);
        var installed = state is LlmModelState.Installed or LlmModelState.Active;
        var detail = HistoryUiFormat.Join(
            installed ? "Installed" : state == LlmModelState.Downloading ? "Downloading" : "Not downloaded",
            "Shared by translation, chats and titles",
            $"{ModelStorageLayout.LlmFolderName}/ in Model storage",
            HistoryUiFormat.Bytes(model.SizeBytes));
        var link = SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Library), model.Title, detail, () => Ctx.Navigate(typeof(ModelsPage), ModelsTab.Translation), "Gemma model");

        var compute = SettingsRows.Segmented(this, SettingsText.GemmaComputeOptions, SettingsText.NormalizeGemmaCompute(Ctx.Live.TranslationComputeBackend), "gemma-compute",
            value => _ = SwitchGemmaComputeAsync(llm, value));
        var computeRow = SettingsRows.StackRow(SettingsRows.Icon(Symbol.Setting), "Run Gemma on",
            "Graphics card is faster but needs a working graphics driver", compute, out _);
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
