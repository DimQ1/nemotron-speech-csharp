using Microsoft.UI.Xaml.Automation;
using SpeechLib.ModelDownload;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>Speech recognition: the model in use, language, silence skipping, compute (readable names) and advanced decoding.</summary>
internal sealed class SpeechSection : SettingsSectionView
{
    private TextBlock? _manageDetail;
    private TextBlock? _waitingCaption;
    private bool _advancedOpen;
    private StackPanel? _advancedHost;

    public SpeechSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var live = Ctx.Live;
        var stack = new StackPanel();

        // ── Model ─────────────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Model"));
        var hasModel = !string.IsNullOrWhiteSpace(live.SelectedModel);
        var descriptor = hasModel ? ModelCatalog.FindBySubfolder(live.SelectedModel) : null;
        var title = hasModel ? ModelCatalog.DescribeFolder(live.SelectedModel) : "No model selected";
        var detail = hasModel
            ? HistoryUiFormat.Join("In use", descriptor?.Tagline ?? "Your own model", $"{ModelStorageLayout.AsrFolderName}/ in Model storage")
            : "Choose or download a model on the Models page";
        var active = SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Library), title, detail, () => Ctx.Navigate(typeof(ModelsPage), ModelsTab.Speech), "Speech model in use");
        var manage = SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Download), "Manage models", "Download, switch or delete", () => Ctx.Navigate(typeof(ModelsPage), ModelsTab.Speech), out _manageDetail);
        stack.Children.Add(SettingsRows.Card(active, manage));

        // ── Recognition ───────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Recognition"));
        var languages = SettingsText.RecognitionLanguages.Select(o => o.Title).ToList();
        var codes = SettingsText.RecognitionLanguages.Select(o => o.Value).ToList();
        var languageIndex = codes.FindIndex(c => string.Equals(c, string.IsNullOrWhiteSpace(live.Language) ? "auto" : live.Language.Trim(), StringComparison.OrdinalIgnoreCase));
        if (languageIndex < 0)
        {
            languages.Add(SettingsText.LanguageTitle(live.Language));
            codes.Add(live.Language);
            languageIndex = codes.Count - 1;
        }
        var choiceAvailable = Ctx.Main.IsLanguageChoiceAvailable;
        var combo = SettingsRows.Combo(languages, languageIndex, "Language", i => Ctx.Change(s => s.Language = codes[i]));
        combo.IsEnabled = choiceAvailable;
        var languageRow = SettingsRows.Row(SettingsRows.Icon(Symbol.Globe), "Language",
            choiceAvailable ? "Auto-detect lets the model decide" : "This model detects the language itself", combo);
        var vad = SettingsRows.Switch(live.UseVad, "Skip silence", on => Ctx.Change(s => s.UseVad = on));
        var vadRow = SettingsRows.Row(SettingsRows.Icon(Symbol.Audio), "Skip silence", "Voice activity detection · saves battery", vad);
        stack.Children.Add(SettingsRows.Card(languageRow, vadRow));

        // ── Compute ───────────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Compute"));
        var current = Ctx.Applier.PendingExecutionProvider ?? SettingsText.NormalizeCompute(live.ExecutionProvider);
        var radios = SettingsText.ComputeOptions
            .Select(o => (UIElement)SettingsRows.Radio(this, "compute", o, o.Value == current, value => Ctx.Change(s => s.ExecutionProvider = value)))
            .ToArray();
        stack.Children.Add(SettingsRows.Card(radios));
        stack.Children.Add(SettingsRows.Note(SettingsText.ReloadCaption));
        _waitingCaption = SettingsRows.Secondary(SettingsText.WaitingForStopCaption);
        _waitingCaption.Margin = new Thickness(4, 4, 4, 0);
        _waitingCaption.Visibility = Ctx.Applier.HasPending ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(_waitingCaption);

        // ── Advanced ──────────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Advanced"));
        _advancedHost = new StackPanel();
        stack.Children.Add(_advancedHost);
        RebuildAdvanced();
        return stack;
    }

    private void RebuildAdvanced()
    {
        if (_advancedHost is null)
            return;
        _advancedHost.Children.Clear();
        var toggle = SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Setting), "Advanced decoding", "Nemotron and Qwen3 only",
            () => { _advancedOpen = !_advancedOpen; RebuildAdvanced(); });
        if (toggle.Content is Grid grid && grid.Children.OfType<FontIcon>().FirstOrDefault() is { } chevron)
            chevron.Glyph = _advancedOpen ? "" : "";
        var rows = new List<UIElement> { toggle };
        if (_advancedOpen)
        {
            var penalty = SettingsText.NearestRepetition(Ctx.Live.RepetitionPenalty);
            var options = SettingsText.RepetitionPenaltyChoices.Select(v => new SettingsOption(SettingsText.RepetitionLabel(v), SettingsText.RepetitionLabel(v))).ToList();
            var segmented = SettingsRows.Segmented(this, options, SettingsText.RepetitionLabel(penalty), "repetition",
                value => Ctx.Change(s => s.RepetitionPenalty = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
            rows.Add(SettingsRows.StackRow(SettingsRows.Icon(Symbol.Document), "Repetition penalty",
                "1.0 = off. Higher values stop looped words; too high drops real repeats.", segmented, out _));
        }
        _advancedHost.Children.Add(SettingsRows.Card([.. rows]));
    }

    public override void OnShown()
    {
        Ctx.Applier.PendingChanged += OnPendingChanged;
        _ = LoadInstalledCountAsync();
    }

    public override void OnHidden() => Ctx.Applier.PendingChanged -= OnPendingChanged;

    private void OnPendingChanged() => DispatcherQueue?.TryEnqueue(() =>
    {
        if (_waitingCaption is not null)
            _waitingCaption.Visibility = Ctx.Applier.HasPending ? Visibility.Visible : Visibility.Collapsed;
    });

    private async Task LoadInstalledCountAsync()
    {
        var layout = ModelStorageLayout.Resolve(Ctx.Live);
        var count = await Task.Run(() => InstalledAsrModels.Scan(layout.AsrScanRoots).Count);
        if (_manageDetail is not null)
        {
            _manageDetail.Text = $"{count} installed · download, switch or delete";
            _manageDetail.Visibility = Visibility.Visible;
        }
    }
}
