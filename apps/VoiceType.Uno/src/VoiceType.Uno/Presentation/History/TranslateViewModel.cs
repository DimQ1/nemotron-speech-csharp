using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using SpeechLib.Translation;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Jobs;

namespace VoiceType.Uno.Presentation.History;

/// <summary>One line of the bilingual transcript (translation big, original small, or the original dimmed while untranslated).</summary>
public sealed class TranslateRowViewModel : ObservableObject
{
    private bool _isCurrent;
    private string? _translation;
    private bool _showOriginal;

    public TranslateRowViewModel(BilingualRow row)
    {
        Start = row.Start;
        TimeText = row.TimeText;
        Original = row.Original;
        _translation = row.Translation;
    }

    public double Start { get; }
    public string TimeText { get; }
    public string Original { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public void SetShowOriginal(bool showOriginal) => Apply(_translation, showOriginal);

    public void Apply(string? translation, bool showOriginal)
    {
        _translation = translation;
        _showOriginal = showOriginal;
        OnPropertyChanged(nameof(MainText));
        OnPropertyChanged(nameof(MainOpacity));
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(HasSecondary));
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>The translation; "…" for a segment that only continues a sentence; the original while not translated yet.</summary>
    public string MainText => _translation is null ? Original : _translation.Length == 0 ? "…" : _translation;

    public double MainOpacity => _translation is null || _translation.Length == 0 ? 0.5 : 1;

    public string SecondaryText => _showOriginal && _translation is not null ? Original : "";
    public bool HasSecondary => SecondaryText.Length > 0;
    public string AutomationName => TimeText + " " + MainText;
}

/// <summary>One entry of the language picker.</summary>
public sealed record TranslateChoice(string Code, string Name);

/// <summary>
/// The Translate tab of the recording page (design §4.3, §8): picks the language, queues the <c>translate</c>
/// job, follows its progress and shows the bilingual transcript of the chosen translation, synced with the player.
/// Store access runs on the thread pool; properties change on the UI thread.
/// </summary>
public sealed class TranslateViewModel : ObservableObject, IDisposable
{
    private readonly RecordingStore _store;
    private readonly JobQueue _jobs;
    private readonly JobRunner _runner;
    private readonly DispatcherQueue _dispatcher;
    private readonly ModelDownloadManager _downloads;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _storeTimer;

    private RecordingManifest? _manifest;
    private string? _viewedVersion;
    private string? _selectedId;
    private (string Version, string Code)? _pendingSelect;
    private string? _rowsFor;
    private int _rowsCount;
    private bool _showOriginal;
    private int _current = -1;
    private int _loadGeneration;

    // Derived state
    private TranslationInfo? _selected;
    private HistoryJob? _job;
    private string _busyText = "";
    private string _waitText = "";
    private double _busyFraction;
    private bool _modelInstalled = true;
    private string _modelText = "";

    public TranslateViewModel(RecordingStore store, JobQueue jobs, JobRunner runner, ModelDownloadManager downloads, DispatcherQueue dispatcher)
    {
        _store = store;
        _jobs = jobs;
        _runner = runner;
        _downloads = downloads;
        _dispatcher = dispatcher;
        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(120);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _storeTimer = dispatcher.CreateTimer();
        _storeTimer.IsRepeating = false;
        _storeTimer.Interval = TimeSpan.FromMilliseconds(300);
        _storeTimer.Tick += async (_, _) => await RefreshAsync();

        _store.Changed += OnStoreChanged;
        _jobs.Changed += OnJobsChanged;
        _runner.StatusChanged += OnJobsChanged;
        _downloads.JobAdded += OnDownloadChanged;
        _downloads.JobUpdated += OnDownloadChanged;
        _downloads.JobFinished += OnDownloadChanged;
        _downloads.JobRemoved += OnDownloadChanged;
    }

    public string RecordingId { get; private set; } = "";

    public ObservableCollection<TranslateRowViewModel> Rows { get; } = [];
    public ObservableCollection<TranslationListItem> Translations { get; } = [];

    public IReadOnlyList<TranslateChoice> Languages { get; } =
        TranslationLanguages.All.Select(l => new TranslateChoice(l.Code, l.Name)).ToList();

    // ── Bindable state ────────────────────────────────────────────────────

    public string? SelectedId => _selectedId;

    /// <summary><c>→ German · from v2</c> or <c>Translate to…</c>.</summary>
    public string PickerText => _selected is null
        ? "Translate to…"
        : $"→ {TranslateTabLogic.LanguageName(_selected.TargetLanguage)} · from {_selected.SourceVersion}";

    public bool HasSelection => _selected is not null;

    /// <summary>Picker row: shown once there is something to pick or a job is running (the empty state has its own button).</summary>
    public bool ShowToolbar => _manifest is not null && (Translations.Count > 0 || IsBusy);
    public bool HasRows => Rows.Count > 0;

    /// <summary>No translation yet and nothing running: the empty state with the hint.</summary>
    public bool IsEmptyState => _manifest is not null && Translations.Count == 0 && !IsBusy && _modelInstalled;

    public bool ShowOriginal
    {
        get => _showOriginal;
        set
        {
            if (!SetProperty(ref _showOriginal, value))
                return;
            ApplyRows();
            OnPropertyChanged(nameof(ShowOriginalText));
        }
    }

    public string ShowOriginalText => _showOriginal ? "Hide original" : "Show original";

    // Model state
    public bool ModelMissing => !_modelInstalled;
    public string ModelText => _modelText;
    public bool CanDownloadModel => !_modelInstalled && _downloads.FindActive(TranslationModelInfo.Key) is null;

    // Job state
    public bool IsBusy => _job is not null;
    public bool IsRunning => _job?.State == JobState.Running;
    public string BusyText => _busyText;
    public string WaitText => _waitText;
    public bool HasWaitText => _waitText.Length > 0;
    public double BusyFraction => _busyFraction;

    /// <summary>A partial translation that no job is working on: offer Continue.</summary>
    public bool CanContinue => _selected is { Status: not TranslationStatuses.Complete } && !IsBusy && _modelInstalled;

    public string PartialText => _selected is { Status: not TranslationStatuses.Complete } s
        ? $"Partial · {s.SentencesDone} / {s.SentencesTotal} sentences"
        : "";

    public bool IsOutdated => _manifest is not null && _selected is not null && TranslateTabLogic.IsOutdated(_manifest, _selected);

    public string OutdatedText => _manifest?.CurrentVersion is { } current && _selected is not null
        ? $"This translation is of {_selected.SourceVersion}; the transcript is now {current}."
        : "";

    public bool CanTranslateCurrent => IsOutdated && !IsBusy && _modelInstalled;

    public bool CanTranslate => _manifest is not null && _modelInstalled && _viewedVersion is not null;

    /// <summary>Version a new translation is made from (the version shown on the Transcript tab).</summary>
    public string? SourceVersion => _viewedVersion ?? _manifest?.CurrentVersion;

    // ── Loading ───────────────────────────────────────────────────────────

    /// <summary>The recording page loaded (or reloaded) the recording.</summary>
    public async Task LoadAsync(string recordingId, string? viewedVersion)
    {
        if (RecordingId != recordingId)
        {
            RecordingId = recordingId;
            _selectedId = null;
            _pendingSelect = null;
            _rowsFor = null;
        }
        _viewedVersion = viewedVersion;
        await RefreshAsync();
    }

    private void OnStoreChanged(string id)
    {
        if (id != RecordingId)
            return;
        _dispatcher.TryEnqueue(() =>
        {
            _storeTimer.Stop();
            _storeTimer.Start();
        });
    }

    private void OnJobsChanged() => _dispatcher.TryEnqueue(() =>
    {
        // Progress ticks arrive often: coalesce.
        if (!_refreshTimer.IsRunning)
            _refreshTimer.Start();
    });

    private void OnDownloadChanged(DownloadJob _) => _dispatcher.TryEnqueue(() =>
    {
        UpdateModelState();
        RaiseState();
    });

    private void UpdateModelState()
    {
        _modelInstalled = GemmaAvailability.IsInstalled;
        var download = _downloads.FindActive(TranslationModelInfo.Key);
        _modelText = download is null
            ? "Translation runs on the device with Gemma 4 E2B (2.4 GB). Download it once, then translate any recording offline."
            : download.TotalBytes > 0 ? $"Downloading the Gemma model… {download.Percent:F0}%" : "Downloading the Gemma model…";
    }

    /// <summary>Reloads manifest, jobs, the chosen translation and the rows.</summary>
    public async Task RefreshAsync()
    {
        _refreshTimer.Stop();
        var id = RecordingId;
        if (id.Length == 0)
            return;
        var generation = ++_loadGeneration;
        UpdateModelState();

        var manifest = await Task.Run(() => _store.Get(id)?.Manifest);
        if (generation != _loadGeneration)
            return;
        _manifest = manifest;
        if (manifest is null)
        {
            Rows.Clear();
            Translations.Clear();
            _selected = null;
            RaiseState();
            return;
        }

        var list = TranslateTabLogic.List(manifest);
        SyncTranslations(list);
        ApplyJob(manifest);

        // Selection: the translation the user just started, the one chosen, else the newest of the viewed version, else the newest.
        if (_pendingSelect is { } pending && manifest.Translations.FirstOrDefault(t => t.SourceVersion == pending.Version
                && string.Equals(t.TargetLanguage, pending.Code, StringComparison.OrdinalIgnoreCase)) is { } started)
        {
            _selectedId = started.Id;
            _pendingSelect = null;
        }
        if (_selectedId is null || manifest.FindTranslation(_selectedId) is null)
        {
            _selectedId = manifest.Translations.Where(t => t.SourceVersion == _viewedVersion).OrderByDescending(t => t.CreatedAt).FirstOrDefault()?.Id
                ?? manifest.Translations.OrderByDescending(t => t.CreatedAt).FirstOrDefault()?.Id;
        }
        _selected = _selectedId is null ? null : manifest.FindTranslation(_selectedId);

        await LoadRowsAsync(id, _selected, generation);
        RaiseState();
    }

    private void SyncTranslations(List<TranslationListItem> list)
    {
        if (Translations.Select(t => t.Id + t.StatusText + t.IsOutdated).SequenceEqual(list.Select(t => t.Id + t.StatusText + t.IsOutdated)))
            return;
        Translations.Clear();
        foreach (var item in list)
            Translations.Add(item);
    }

    private void ApplyJob(RecordingManifest manifest)
    {
        _job = TranslateTabLogic.ActiveJob(_jobs.Jobs, RecordingId);
        _waitText = "";
        _busyText = "";
        _busyFraction = 0;
        if (_job is null)
            return;

        var code = TranslationLanguageCodes.Normalize(_job.Parameters.TargetLanguage) ?? "";
        var name = TranslateTabLogic.LanguageName(code);
        var info = manifest.Translations.FirstOrDefault(t =>
            t.SourceVersion == (_job.Parameters.SourceVersion ?? manifest.CurrentVersion)
            && string.Equals(t.TargetLanguage, code, StringComparison.OrdinalIgnoreCase));
        _busyText = TranslateTabLogic.ProgressText(name, info?.SentencesDone ?? 0, info?.SentencesTotal ?? 0);
        _busyFraction = info is { SentencesTotal: > 0 } ? (double)info.SentencesDone / info.SentencesTotal : _job.Progress;

        if (_job.State == JobState.Running)
        {
            // "Waiting for live translation" arrives as the executor's detail while the host serves Live.
            if (_runner.Status is { } status && status.JobId == _job.Id && !string.IsNullOrEmpty(status.Detail))
                _waitText = status.Detail;
        }
        else if (_job.State == JobState.Paused)
        {
            _waitText = "Paused in the Queue";
        }
        else
        {
            var reason = _runner.GetWaitReason(_job, _jobs.Jobs);
            _waitText = JobDisplay.WaitReasonText(_job, reason, DateTimeOffset.Now) ?? "Queued · starts next";
        }
    }

    private async Task LoadRowsAsync(string id, TranslationInfo? selected, int generation)
    {
        if (selected is null)
        {
            Rows.Clear();
            _rowsFor = null;
            _rowsCount = 0;
            return;
        }

        var (transcript, document) = await Task.Run(() =>
        {
            try
            {
                return (_store.LoadTranscript(id, selected.SourceVersion), (TranslationDocument?)_store.LoadTranslation(id, selected.Id));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                Console.WriteLine($"[VoiceType.Uno] Translation {selected.Id} could not be read: {ex.Message}");
                return (null, null);
            }
        });
        if (generation != _loadGeneration)
            return;
        if (transcript is null)
        {
            Rows.Clear();
            return;
        }

        var rows = TranslateTabLogic.BuildRows(transcript.Segments, document);
        var key = id + "/" + selected.SourceVersion;
        if (_rowsFor == key && _rowsCount == rows.Count && Rows.Count == rows.Count)
        {
            for (var i = 0; i < rows.Count; i++)
                Rows[i].Apply(rows[i].Translation, _showOriginal); // same segments: update in place, no flicker
        }
        else
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                var vm = new TranslateRowViewModel(row);
                vm.Apply(row.Translation, _showOriginal);
                Rows.Add(vm);
            }
            _current = -1;
        }
        _rowsFor = key;
        _rowsCount = rows.Count;
    }

    private void ApplyRows()
    {
        foreach (var row in Rows)
            row.SetShowOriginal(_showOriginal);
    }


    private void RaiseState()
    {
        foreach (var name in new[]
                 {
                     nameof(PickerText), nameof(HasSelection), nameof(HasRows), nameof(IsEmptyState), nameof(ModelMissing),
                     nameof(ModelText), nameof(CanDownloadModel), nameof(IsBusy), nameof(IsRunning), nameof(BusyText),
                     nameof(WaitText), nameof(HasWaitText), nameof(BusyFraction), nameof(CanContinue), nameof(PartialText),
                     nameof(IsOutdated), nameof(OutdatedText), nameof(CanTranslateCurrent), nameof(CanTranslate),
                     nameof(SourceVersion), nameof(SelectedId), nameof(ShowToolbar), nameof(ShowOriginalText),
                 })
            OnPropertyChanged(name);
    }

    // ── Actions ───────────────────────────────────────────────────────────

    public void Select(string translationId)
    {
        _selectedId = translationId;
        _ = RefreshAsync();
    }

    /// <summary>
    /// Translates the viewed version into <paramref name="code"/>. Returns false when this language and version already
    /// has a complete translation: the caller shows that one instead (and asks before "Translate again").
    /// </summary>
    public bool TryStart(string code)
    {
        if (_manifest is null || SourceVersion is not { } version)
            return false;
        var normalized = TranslationLanguageCodes.Normalize(code) ?? code;
        var existing = _manifest.Translations.FirstOrDefault(t => t.SourceVersion == version
            && string.Equals(t.TargetLanguage, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is { Status: TranslationStatuses.Complete })
        {
            Select(existing.Id);
            return false;
        }
        Enqueue(version, normalized);
        return true;
    }

    /// <summary>Starts a translation over (replaces the saved one of that language and version).</summary>
    public void TranslateAgain(string version, string code) => Enqueue(version, code);

    /// <summary>Continues the chosen partial translation, or (outdated) translates the current version into its language.</summary>
    public void Continue()
    {
        if (_selected is { } selected)
            Enqueue(selected.SourceVersion, selected.TargetLanguage);
    }

    public void TranslateCurrentVersion()
    {
        if (_selected is { } selected && _manifest?.CurrentVersion is { } current)
        {
            _pendingSelect = (current, selected.TargetLanguage);
            Enqueue(current, selected.TargetLanguage);
        }
    }

    private void Enqueue(string version, string code)
    {
        if (_manifest is null)
            return;
        try
        {
            _pendingSelect = (version, code);
            HistoryLlmJobs.EnqueueTranslate(_jobs, _manifest, version, code);
            _runner.Poke();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[VoiceType.Uno] Translation was not queued: {ex.Message}");
        }
        _ = RefreshAsync();
    }

    /// <summary>Stops the running or queued job; what was translated so far stays as a partial translation.</summary>
    public void Stop()
    {
        if (_job is { } job)
            _jobs.Cancel(job.Id);
        _ = RefreshAsync();
    }

    public async Task DeleteSelectedAsync()
    {
        if (_selected is not { } selected)
            return;
        var id = RecordingId;
        if (_job is { } job
            && string.Equals(TranslationLanguageCodes.Normalize(job.Parameters.TargetLanguage), selected.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            _jobs.Cancel(job.Id);
        try
        {
            await Task.Run(() => _store.DeleteTranslation(id, selected.Id));
        }
        catch (Exception ex) when (ex is IOException or KeyNotFoundException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[VoiceType.Uno] Translation could not be deleted: {ex.Message}");
        }
        _selectedId = null;
        await RefreshAsync();
    }

    public TranslationInfo? Selected => _selected;

    // ── Player sync ───────────────────────────────────────────────────────

    /// <summary>Highlights the row playing at <paramref name="seconds"/>; returns its index (or -1).</summary>
    public int Highlight(double seconds)
    {
        int lo = 0, hi = Rows.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Rows[mid].Start <= seconds)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        SetCurrent(found);
        return found;
    }

    public void SetCurrent(int index)
    {
        if (index == _current)
            return;
        if (_current >= 0 && _current < Rows.Count)
            Rows[_current].IsCurrent = false;
        _current = index;
        if (index >= 0 && index < Rows.Count)
            Rows[index].IsCurrent = true;
    }

    public void DownloadModel()
    {
        App.Services.GetRequiredService<Presentation.MainViewModel>().EnqueueTranslationModelDownload();
        UpdateModelState();
        RaiseState();
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _jobs.Changed -= OnJobsChanged;
        _runner.StatusChanged -= OnJobsChanged;
        _downloads.JobAdded -= OnDownloadChanged;
        _downloads.JobUpdated -= OnDownloadChanged;
        _downloads.JobFinished -= OnDownloadChanged;
        _downloads.JobRemoved -= OnDownloadChanged;
        _refreshTimer.Stop();
        _storeTimer.Stop();
    }
}
