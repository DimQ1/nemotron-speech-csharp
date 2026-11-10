using System.Globalization;
using VoiceType.Uno.History;
using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Jobs;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// "Re-recognize" sheet (design §4.7 / §9, mockup <c>*-rerecognize-sheet</c>, batch variant of
/// <c>*-reprocess-sheet</c> without the speaker/summary steps, which come later): pick an installed
/// ASR model (catalog WER, "Used for vN"), language, VAD, "make current", and when to run (now,
/// when idle, when charging, a nightly time window); shows the estimate from the model speed and
/// the audio duration. Start enqueues one job, or one batch for several recordings.
/// <code>await new ReprocessSheet(recordingIds) { XamlRoot = XamlRoot }.ShowAsync();</code>
/// </summary>
public sealed partial class ReprocessSheet : ContentDialog
{
    private readonly RecordingStore _store;
    private readonly JobQueue _queue;
    private readonly JobRunner _runner;
    private readonly AppSettings _settings;
    private readonly IDeviceConditions? _device;
    private readonly List<RecordingManifest> _recordings = [];
    private readonly List<(RadioButton Button, InstalledAsrModel Model)> _models = [];
    private readonly List<string> _languageValues = [];
    private readonly int _requested;

    private readonly IReprocessChainEnqueuer? _chain;
    private readonly bool _speakersOnly;

    /// <param name="speakersOnly">"Detect again…" from the Speakers sheet: the Speech step starts switched off.</param>
    public ReprocessSheet(IReadOnlyList<string> recordingIds, bool speakersOnly = false)
    {
        ArgumentNullException.ThrowIfNull(recordingIds);
        InitializeComponent();

        var services = App.Services;
        _store = services.GetRequiredService<RecordingStore>();
        _queue = services.GetRequiredService<JobQueue>();
        _runner = services.GetRequiredService<JobRunner>();
        _settings = services.GetRequiredService<SettingsStore>().Snapshot();
        _device = services.GetService<IDeviceConditions>();
        _speakersOnly = speakersOnly;
        _chain = SpeakerServices.Chain;

        _requested = recordingIds.Distinct(StringComparer.Ordinal).Count();
        foreach (var id in recordingIds.Distinct(StringComparer.Ordinal))
        {
            if (_store.Get(id)?.Manifest is { } manifest && manifest.HasAudio)
                _recordings.Add(manifest);
        }

        BuildChain();
        BuildHeader();
        BuildModels();
        BuildLanguages();
        BuildWhen();
        VadSwitch.IsOn = _settings.UseVad;
        MakeCurrentSwitch.IsOn = _recordings.Count > 1;
        MakeCurrentLabel.Text = _recordings.Count > 1 ? Loc.T("Recording.Reprocess.MakeCurrentMany") : Loc.T("Recording.Reprocess.MakeCurrent");
        ApplySteps();
        UpdateEstimate();
        UpdateStartEnabled();
    }

    /// <summary>Jobs added by Start (empty when cancelled).</summary>
    public IReadOnlyList<HistoryJob> Enqueued { get; private set; } = [];

    // ── Build ───────────────────────────────────────────────────────────────

    /// <summary>Chain controls (Speech → Speakers → Analysis); hidden while no <see cref="IReprocessChainEnqueuer"/> is registered.</summary>
    private void BuildChain()
    {
        if (_chain is null)
            return;
        ChainPanel.Visibility = Visibility.Visible;
        var speakers = SpeakerServices.Current;
        var modelReady = speakers?.IsModelAvailable == true;
        SpeechStepSwitch.IsOn = !_speakersOnly;
        SpeakersStepSwitch.IsOn = _speakersOnly || modelReady;
        SpeakersStepSwitch.IsEnabled = modelReady;
        if (!modelReady)
        {
            SpeakersStepSwitch.IsOn = false;
            SpeakersStepHint.Text = Loc.T("Recording.Reprocess.SpeakerModelMissing");
        }
        AnalysisStepSwitch.IsOn = false;
        AnalysisStepSwitch.IsEnabled = VoiceType.Uno.Services.Jobs.GemmaAvailability.IsInstalled;
    }

    private void Steps_Toggled(object sender, RoutedEventArgs e) => ApplySteps();

    /// <summary>The model, language and options sections matter only when the Speech step is on.</summary>
    private void ApplySteps()
    {
        if (SpeechSection is null)
            return;
        var speech = _chain is null || SpeechStepSwitch.IsOn;
        SpeechSection.Visibility = speech ? Visibility.Visible : Visibility.Collapsed;
        SpeakersPresetBox.Visibility = _chain is not null && SpeakersStepSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (_chain is not null)
        {
            TitleText.Text = speech ? Loc.T("Recording.Reprocess.TitleChain") : SpeakersStepSwitch.IsOn ? Loc.T("Recording.Reprocess.TitleSpeakers") : Loc.T("Recording.Reprocess.TitleChain");
            if (!speech)
                SubtitleText.Text = Loc.T("Recording.Reprocess.SubtitleNoSpeech");
        }
        UpdateEstimate();
        UpdateStartEnabled();
    }

    private void BuildHeader()
    {
        if (_recordings.Count <= 1)
        {
            TitleText.Text = Loc.T("Recording.Reprocess.Title");
            SubtitleText.Text = _recordings.Count == 1
                ? Loc.F("Recording.Reprocess.SubtitleCreates", ReprocessPlan.NextVersionId(_recordings[0]))
                : Loc.T("Recording.Reprocess.SubtitleCreatesNew");
        }
        else
        {
            TitleText.Text = Loc.F("Recording.Reprocess.TitleMany", _recordings.Count);
            SubtitleText.Text = Loc.T("Recording.Reprocess.SubtitleMany");
        }
        if (_requested > _recordings.Count)
        {
            var skipped = _requested - _recordings.Count;
            SubtitleText.Text += " · " + Loc.Plural("Recording.Reprocess.Skipped", skipped);
        }
    }

    private void BuildModels()
    {
        var installed = InstalledAsrModels.Scan(InstalledAsrModels.DefaultRoots(_settings));
        NoModelsText.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RadioButton? preferred = null;
        foreach (var model in installed)
        {
            var details = new List<string>();
            if (ReprocessPlan.UsedText(model.Folder, _recordings) is { } used)
                details.Add(used);
            if (model.WerPercent is { } wer)
                details.Add(Loc.F("Recording.Reprocess.Wer", wer));
            if (model.Descriptor?.Tagline is { Length: > 0 } tagline && details.Count < 2)
                details.Add(tagline);

            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = model.Title, TextWrapping = TextWrapping.Wrap });
            if (details.Count > 0)
                content.Children.Add(new TextBlock { Text = string.Join(" · ", details), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

            var button = new RadioButton { GroupName = "Model", Content = content, MinHeight = 44, Tag = model };
            button.Checked += (_, _) => UpdateEstimate();
            ModelList.Children.Add(button);
            _models.Add((button, model));

            // Default: the first model none of these recordings was recognized with yet.
            if (preferred is null && ReprocessPlan.UsedText(model.Folder, _recordings) is null)
                preferred = button;
        }
        (preferred ?? _models.FirstOrDefault().Button)?.SetValue(RadioButton.IsCheckedProperty, true);
    }

    private void BuildLanguages()
    {
        string keepText;
        if (_recordings.Count == 1)
        {
            var source = _recordings[0].Current;
            var language = OfflineRecognitionJob.ResolveLanguage(null, source);
            keepText = source?.Id is { } sourceId
                ? Loc.F("Recording.Reprocess.KeepLanguageOf", LanguageName(language), sourceId)
                : Loc.F("Recording.Reprocess.KeepLanguage", LanguageName(language));
        }
        else
        {
            keepText = Loc.T("Recording.Reprocess.KeepEachLanguage");
        }
        AddLanguage(keepText, OfflineRecognitionJob.KeepLanguage);
        AddLanguage(Loc.T("Recording.Reprocess.AutoDetect"), OfflineRecognitionJob.AutoLanguage);
        foreach (var code in SettingsText.LanguageCodes.Where(c => c != "auto"))
            AddLanguage(LanguageName(code), code);
        LanguageBox.SelectedIndex = 0;
    }

    private void AddLanguage(string text, string value)
    {
        LanguageBox.Items.Add(text);
        _languageValues.Add(value);
    }

    private void BuildWhen()
    {
        if (_runner.Status is not null)
            WhenNowText.Text = Loc.T("Recording.Reprocess.NowAfterRunning");
        if (_device is { HasBattery: false })
            WhenChargingHint.Text = Loc.T("Recording.Reprocess.NoBattery");
        WindowHint.Text = OperatingSystem.IsAndroid()
            ? Loc.T("Recording.Reprocess.WindowHintAndroid")
            : Loc.T("Recording.Reprocess.WindowHintDesktop");
        for (var minutes = 0; minutes < 24 * 60; minutes += 30)
        {
            var text = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes)).ToString("HH:mm", CultureInfo.InvariantCulture);
            WindowFrom.Items.Add(text);
            WindowTo.Items.Add(text);
        }
        WindowFrom.SelectedIndex = 2;   // 01:00
        WindowTo.SelectedIndex = 12;    // 06:00
        WindowCharging.IsChecked = OperatingSystem.IsAndroid();
    }

    // ── Events ──────────────────────────────────────────────────────────────

    private void When_Changed(object sender, RoutedEventArgs e)
    {
        if (WindowPanel is not null)
            WindowPanel.Visibility = WhenWindow.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WindowFrom.SelectedIndex >= 0 && WindowTo.SelectedIndex >= 0)
            WindowHint.Text = Loc.F(OperatingSystem.IsAndroid() ? "Recording.Reprocess.WindowTextAndroid" : "Recording.Reprocess.WindowTextDesktop",
                JobDisplay.WindowText(WindowTime(WindowFrom), WindowTime(WindowTo)));
    }

    private void Start_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            if (_recordings.Count == 0)
                throw new InvalidOperationException(Loc.T("Recording.Reprocess.NoAudio"));
            var speech = _chain is null || SpeechStepSwitch.IsOn;
            var speakers = _chain is not null && SpeakersStepSwitch.IsOn;
            var analysis = _chain is not null && AnalysisStepSwitch.IsOn;
            if (!speech && !speakers && !analysis)
                throw new InvalidOperationException(Loc.T("Recording.Reprocess.NoStep"));
            var model = speech ? SelectedModel() ?? throw new InvalidOperationException(Loc.T("Recording.Reprocess.NoModel")) : null;

            var parameters = model is null ? null : new JobParameters
            {
                ModelFolder = model.Folder,
                CatalogRepoId = model.Descriptor?.RepoId,
                Language = _languageValues[Math.Max(0, LanguageBox.SelectedIndex)],
                MakeCurrent = MakeCurrentSwitch.IsOn,
                Settings = JsonValues.Settings((OfflineRecognitionJob.SettingKeys.UseVad, VadSwitch.IsOn)),
            };
            var condition = BuildCondition();
            var ids = _recordings.Select(r => r.Id).ToList();
            if (_chain is not null && (speakers || analysis || !speech))
            {
                // One chain per recording: Speech → Speakers → Analysis (design §11.4). Built by the chain adapter.
                Enqueued = _chain.Enqueue(new ReprocessChainRequest(ids, parameters, condition, speakers,
                    SpeakersPresetBox.SelectedIndex == 1 ? "fast" : "offline", analysis));
                _runner.Poke();
                return;
            }
            Enqueued = ids.Count == 1
                ? [_queue.Enqueue(HistoryJobKind.Rerecognize, ids[0], parameters!, condition)]
                : _queue.EnqueueBatch(HistoryJobKind.Rerecognize, ids, parameters!, condition);
            _runner.Poke();
        }
        catch (InvalidOperationException ex)
        {
            args.Cancel = true;
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private JobCondition BuildCondition()
    {
        if (WhenIdle.IsChecked == true)
            return JobCondition.WhenIdle();
        if (WhenCharging.IsChecked == true)
            return JobCondition.WhenCharging();
        if (WhenWindow.IsChecked == true)
        {
            var window = JobCondition.Window(WindowTime(WindowFrom), WindowTime(WindowTo));
            window.RequireCharging = WindowCharging.IsChecked == true;
            return window;
        }
        return JobCondition.Now();
    }

    private static TimeOnly WindowTime(ComboBox box) =>
        TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Max(0, box.SelectedIndex) * 30));

    private InstalledAsrModel? SelectedModel() =>
        _models.FirstOrDefault(m => m.Button.IsChecked == true).Model;

    private void UpdateEstimate()
    {
        if (EstimateText is null)
            return;
        if (_chain is not null && !SpeechStepSwitch.IsOn)
        {
            EstimateText.Text = Loc.T("Recording.Reprocess.ChainEstimate");
            return;
        }
        var model = SelectedModel();
        if (model is null || _recordings.Count == 0)
        {
            EstimateText.Text = "";
            return;
        }
        var measured = ReprocessPlan.MeasuredRealTimeFactor(model.Folder, _recordings);
        // The catalog speeds were measured on a desktop: scale them by how slow this device proved to be.
        var slowdown = ReprocessPlan.DeviceSlowdown(
            _recordings,
            folder => _models.FirstOrDefault(m => string.Equals(m.Model.Folder, folder, StringComparison.OrdinalIgnoreCase)).Model?.RealTimeFactor,
            OperatingSystem.IsAndroid() ? ReprocessPlan.PhoneSlowdown : 1.0);
        var estimate = ReprocessPlan.Estimate(_recordings, measured, model.RealTimeFactor, slowdown);
        EstimateText.Text = Loc.F(measured is null ? "Recording.Reprocess.EstimateApprox" : "Recording.Reprocess.Estimate",
            JobDisplay.FormatSpan(estimate));

        // Parakeet has no language input: the choice would be silently ignored, so it is disabled.
        var languageChoice = ReprocessPlan.SupportsLanguageChoice(model.Family);
        LanguageBox.IsEnabled = languageChoice;
        LanguageHint.Visibility = languageChoice ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStartEnabled()
    {
        var speech = _chain is null || SpeechStepSwitch.IsOn;
        var anyStep = speech || (_chain is not null && (SpeakersStepSwitch.IsOn || AnalysisStepSwitch.IsOn));
        IsPrimaryButtonEnabled = (!speech || _models.Count > 0) && anyStep && _recordings.Count > 0 && !_queue.IsReadOnly;
        if (_queue.IsReadOnly)
        {
            ErrorText.Text = Loc.T("Recording.Reprocess.QueueReadOnly");
            ErrorText.Visibility = Visibility.Visible;
        }
        else if (_recordings.Count == 0)
        {
            ErrorText.Text = Loc.T("Recording.Reprocess.NoAudio");
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private static string LanguageName(string code)
    {
        if (string.Equals(code, OfflineRecognitionJob.AutoLanguage, StringComparison.OrdinalIgnoreCase))
            return Loc.T("Recording.Reprocess.AutoDetect");
        try
        {
            return LanguageNames.Of(code) is { } name && !string.Equals(name, code, StringComparison.OrdinalIgnoreCase)
                ? name
                : CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code.ToUpperInvariant();
        }
    }
}
