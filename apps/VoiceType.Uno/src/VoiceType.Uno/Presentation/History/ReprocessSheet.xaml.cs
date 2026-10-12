using System.Globalization;
using VoiceType.Uno.History;
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

    public ReprocessSheet(IReadOnlyList<string> recordingIds)
    {
        ArgumentNullException.ThrowIfNull(recordingIds);
        InitializeComponent();

        var services = App.Services;
        _store = services.GetRequiredService<RecordingStore>();
        _queue = services.GetRequiredService<JobQueue>();
        _runner = services.GetRequiredService<JobRunner>();
        _settings = services.GetRequiredService<SettingsStore>().Snapshot();
        _device = services.GetService<IDeviceConditions>();

        _requested = recordingIds.Distinct(StringComparer.Ordinal).Count();
        foreach (var id in recordingIds.Distinct(StringComparer.Ordinal))
        {
            if (_store.Get(id)?.Manifest is { } manifest && manifest.HasAudio)
                _recordings.Add(manifest);
        }

        BuildHeader();
        BuildModels();
        BuildLanguages();
        BuildWhen();
        VadSwitch.IsOn = _settings.UseVad;
        MakeCurrentSwitch.IsOn = _recordings.Count > 1;
        MakeCurrentLabel.Text = _recordings.Count > 1 ? "Make new versions current" : "Make it current when done";
        UpdateEstimate();
        UpdateStartEnabled();
    }

    /// <summary>Jobs added by Start (empty when cancelled).</summary>
    public IReadOnlyList<HistoryJob> Enqueued { get; private set; } = [];

    // ── Build ───────────────────────────────────────────────────────────────

    private void BuildHeader()
    {
        if (_recordings.Count <= 1)
        {
            TitleText.Text = "Re-recognize audio";
            var next = _recordings.Count == 1 ? ReprocessPlan.NextVersionId(_recordings[0]) : "a new version";
            SubtitleText.Text = $"Creates {next} · the current version stays until you switch";
        }
        else
        {
            TitleText.Text = $"Re-recognize {_recordings.Count} recordings";
            SubtitleText.Text = "One batch · each recording gets a new version";
        }
        if (_requested > _recordings.Count)
        {
            var skipped = _requested - _recordings.Count;
            SubtitleText.Text += skipped == 1
                ? " · 1 recording has no audio and is skipped"
                : $" · {skipped} recordings have no audio and are skipped";
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
                details.Add("WER " + wer.ToString("0.0", CultureInfo.InvariantCulture) + "%");
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
            keepText = $"{LanguageName(language)} (same as {source?.Id ?? "the recording"})";
        }
        else
        {
            keepText = "Keep each recording's language";
        }
        AddLanguage(keepText, OfflineRecognitionJob.KeepLanguage);
        AddLanguage("Auto-detect", OfflineRecognitionJob.AutoLanguage);
        foreach (var code in SettingsViewModel.DefaultLanguageOptions.Where(c => c != "auto"))
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
            WhenNowText.Text = "Now — after the running job";
        if (_device is { HasBattery: false })
            WhenChargingHint.Text = "This device has no battery: starts right away";
        WindowHint.Text = OperatingSystem.IsAndroid()
            ? "Every night; runs while VoiceType is open or in the background"
            : "Every night, while VoiceType is running";
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
            WindowHint.Text = JobDisplay.WindowText(WindowTime(WindowFrom), WindowTime(WindowTo))
                + (OperatingSystem.IsAndroid() ? " · continues the next night" : " · while VoiceType is running");
    }

    private void Start_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            var model = SelectedModel() ?? throw new InvalidOperationException("Choose a speech model.");
            if (_recordings.Count == 0)
                throw new InvalidOperationException("None of these recordings has audio to re-recognize.");

            var parameters = new JobParameters
            {
                ModelFolder = model.Folder,
                CatalogRepoId = model.Descriptor?.RepoId,
                Language = _languageValues[Math.Max(0, LanguageBox.SelectedIndex)],
                MakeCurrent = MakeCurrentSwitch.IsOn,
                Settings = JsonValues.Settings((OfflineRecognitionJob.SettingKeys.UseVad, VadSwitch.IsOn)),
            };
            var condition = BuildCondition();
            var ids = _recordings.Select(r => r.Id).ToList();
            Enqueued = ids.Count == 1
                ? [_queue.Enqueue(HistoryJobKind.Rerecognize, ids[0], parameters, condition)]
                : _queue.EnqueueBatch(HistoryJobKind.Rerecognize, ids, parameters, condition);
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
        var approximate = measured is null ? "≈ " : "";
        EstimateText.Text = $"{approximate}{JobDisplay.FormatSpan(estimate)} on this device · runs from the Queue";

        // Parakeet has no language input: the choice would be silently ignored, so it is disabled.
        var languageChoice = ReprocessPlan.SupportsLanguageChoice(model.Family);
        LanguageBox.IsEnabled = languageChoice;
        LanguageHint.Visibility = languageChoice ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStartEnabled()
    {
        IsPrimaryButtonEnabled = _models.Count > 0 && _recordings.Count > 0 && !_queue.IsReadOnly;
        if (_queue.IsReadOnly)
        {
            ErrorText.Text = "The queue was saved by a newer VoiceType and is read-only in this version.";
            ErrorText.Visibility = Visibility.Visible;
        }
        else if (_recordings.Count == 0)
        {
            ErrorText.Text = "None of these recordings has audio to re-recognize.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private static string LanguageName(string code)
    {
        if (string.Equals(code, OfflineRecognitionJob.AutoLanguage, StringComparison.OrdinalIgnoreCase))
            return "Auto-detect";
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code.ToUpperInvariant();
        }
    }
}
