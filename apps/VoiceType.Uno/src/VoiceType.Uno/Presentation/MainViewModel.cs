using SpeechLib.ModelDownload;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceType.Hotkeys;
using VoiceType.Hotkeys.XdgPortal;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Platform;
using VoiceType.Uno.Services.Platform.Linux;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// Main dictation ViewModel. Mirrors the WinUI MainViewModel behavior
/// (start/stop, mute, model lifecycle, text injection toggle, language)
/// but depends on platform abstractions instead of Win32 services.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly RecognitionService _recognition;
    private readonly SettingsStore _settingsStore;
    private readonly ModelDownloadManager _downloads;
    private IGlobalHotkeyService _hotkeys;
    private readonly IPlatformTextInjector _textInjector;
    private readonly ITrayIndicator _tray;
    private readonly TranslationService _translation;
    private readonly IUiScheduler _scheduler;

    // Speech-to-transcript merging (throttled partials, manual-input hand-off, "speech
    // continues after the text already in the field") lives in the coordinator; this
    // class only projects its result into FloatingText.
    private readonly TranscriptCoordinator _transcript;

    /// <summary>
    /// The live settings instance. Always read through the store so a replaced instance
    /// can never be observed here.
    /// </summary>
    private AppSettings Settings => _settingsStore.Current;

    private int _toggleHotkeyId;
    private int _muteHotkeyId;
    private int _injectTextHotkeyId;

    // Applying a settings snapshot is serialized (one at a time, newest wins) by the
    // gate plus the version counter; _isProjectingSettingsSnapshot suppresses the
    // per-property persistence handlers while the snapshot is written onto the
    // observable properties, because the snapshot path persists once.
    private readonly SemaphoreSlim _settingsApplyGate = new(1, 1);
    private int _settingsApplyVersion;
    private bool _isProjectingSettingsSnapshot;

    private Task? _modelInitializationTask;

    public MainViewModel(
        RecognitionService recognition,
        SettingsStore settingsStore,
        ModelDownloadManager downloads,
        IGlobalHotkeyService hotkeys,
        IPlatformTextInjector textInjector,
        ITrayIndicator tray,
        TranslationService translation,
        IUiScheduler scheduler)
    {
        _recognition = recognition;
        _settingsStore = settingsStore;
        _downloads = downloads;
        _hotkeys = hotkeys;
        _textInjector = textInjector;
        _tray = tray;
        _translation = translation;
        _scheduler = scheduler;
        _transcript = new TranscriptCoordinator(scheduler, () => FloatingText ?? "");

        _selectedLanguage = Settings.Language;
        IsTextInjectionEnabled = Settings.IsTextInjectionEnabled;
        IsAutoScrollEnabled = Settings.IsAutoScrollEnabled;
        AlwaysOnTop = Settings.AlwaysOnTop;
        IsTranslationEnabled = Settings.TranslationEnabled;
        _translationTargetLanguage = Settings.TranslationTargetLanguage;
        _translation.SetTargetLanguage(Settings.TranslationTargetLanguage);
        _translation.SetAdditionalSystemPrompt(Settings.TranslationSystemPrompt);
        _translation.SetComputeBackend(Settings.TranslationComputeBackend);
        _translation.SetSourceLanguage(Settings.Language);

        // The coordinator owns the ordering rules; this class mirrors the composed text
        // and reacts to a committed final result.
        _transcript.TextChanged += text =>
        {
            FloatingText = text;
            if (IsTranslationEnabled)
                _translation.Feed(text);
        };
        _transcript.FinalCommitted += text =>
        {
            if (IsTextInjectionEnabled && !string.IsNullOrEmpty(text))
                InjectOffUiThread(text);

            if (IsTranslationEnabled)
                _ = _translation.FlushAsync();
        };

        _recognition.PartialResult += _transcript.OnPartialResult;
        _recognition.FinalResult += _transcript.OnFinalResult;
        _recognition.Stopped += () => _scheduler.Post(() =>
        {
            IsRecording = false;
            StatusText = "Ready";
            _tray.SetRecording(false);
        });
        _recognition.ModelStateChanged += state => _scheduler.Post(() =>
        {
            IsModelLoading = state == ModelLifecycleState.Loading;
            IsModelReady = state == ModelLifecycleState.Loaded;
            ModelStatusText = state switch
            {
                ModelLifecycleState.Unloaded => "No model loaded",
                ModelLifecycleState.Loading => "Loading model...",
                ModelLifecycleState.Loaded => $"Model ready — {ModelDisplayName}",
                ModelLifecycleState.Error => "Model load error",
                _ => ""
            };
            OnPropertyChanged(nameof(RecordButtonText));
        });
        _recognition.Error += exception => _scheduler.Post(() =>
            StatusText = $"Recognition error: {exception.Message}");

        // Aggregated progress for the whole download queue (ASR + translation in
        // parallel) comes from the shared manager; the same events drive the
        // per-model status line under the model banner.
        _downloads.JobAdded += _ => _scheduler.Post(RefreshQueueProgress);
        _downloads.JobUpdated += job => _scheduler.Post(() =>
        {
            RefreshQueueProgress();
            ReportModelDownloadProgress(job);
        });
        _downloads.JobFinished += job => _scheduler.Post(() =>
        {
            RefreshQueueProgress();
            ReportModelDownloadProgress(job);
        });
        _downloads.JobRemoved += _ => _scheduler.Post(RefreshQueueProgress);

        // Global hotkeys: presses arrive via the HotkeyPressed event. On Linux
        // the XDG portal is swapped in asynchronously (consent dialog on first
        // run); on Windows RegisterHotKey is wired in the composition root.
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        // Tray indicator: register with the desktop environment; activation
        // (icon click) toggles recording like the main button.
        _tray.Activated += () => _scheduler.Post(() => _ = ToggleAsync());
        _ = RunInBackgroundAsync(_tray.InitializeAsync(), "Tray indicator");

        // Live translation: stream transcript deltas through the LiteRT-LM
        // server; translated text is displayed (and injectable) alongside the
        // original transcript.
        _translation.TranslationChanged += text => _scheduler.Post(() => TranslatedText = text);
        _translation.StatusChanged += status => _scheduler.Post(() => TranslationStatusText = status);

        _ = RunInBackgroundAsync(InitializeHotkeysAsync(), "Global hotkeys");

        RefreshModelBanners();
        RefreshQueueProgress();

        IsModelLoading = true;
        ModelStatusText = "Checking model...";
        _modelInitializationTask = InitializeModelAsync();
    }

    private async Task InitializeHotkeysAsync()
    {
        var service = _hotkeys;

        // On Linux, connect the XDG GlobalShortcuts portal in the background and
        // swap it in place of the startup Null object.
        if (!service.IsAvailable && OperatingSystem.IsLinux())
        {
            var xdg = await XdgGlobalShortcutsService.TryCreateAsync().ConfigureAwait(false);
            if (xdg is not null)
            {
                _hotkeys = xdg;
                xdg.HotkeyPressed += OnHotkeyPressed;
                service = xdg;
            }
        }

        if (!service.IsAvailable)
            return;

        if (!string.IsNullOrWhiteSpace(Settings.ToggleHotkey))
            _toggleHotkeyId = await service.RegisterAsync(Settings.ToggleHotkey).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(Settings.MuteHotkey))
            _muteHotkeyId = await service.RegisterAsync(Settings.MuteHotkey).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(Settings.InjectTextHotkey))
            _injectTextHotkeyId = await service.RegisterAsync(Settings.InjectTextHotkey).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a fire-and-forget startup task, reporting failures on the status line
    /// instead of leaving an unobserved task exception behind.
    /// </summary>
    private async Task RunInBackgroundAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _scheduler.Post(() => StatusText = $"{operation} error: {ex.Message}");
        }
    }

    private async Task ReregisterHotkeysAsync()
    {
        try
        {
            if (_hotkeys.IsAvailable)
                await _hotkeys.UnregisterAllAsync().ConfigureAwait(false);

            _toggleHotkeyId = 0;
            _muteHotkeyId = 0;
            _injectTextHotkeyId = 0;

            await InitializeHotkeysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _scheduler.Post(() => StatusText = $"Hotkey error: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists a single settings change. Every write goes through the store, which owns
    /// the live settings instance and serializes the file writes.
    /// </summary>
    private void Persist(Action<AppSettings> mutate) =>
        _settingsStore.Post(mutate, ex => _scheduler.Post(() => StatusText = $"Settings error: {ex.Message}"));

    /// <summary>
    /// Text injection can block for ~80 ms on Linux (clipboard-owner hand-off plus a
    /// synthetic paste chord). Run it off the UI thread so the transcript stays
    /// responsive while the keystroke is delivered.
    /// </summary>
    private void InjectOffUiThread(string text) => _ = Task.Run(() =>
    {
        try
        {
            _textInjector.Inject(text);
        }
        catch (Exception ex)
        {
            _scheduler.Post(() => StatusText = $"Injection error: {ex.Message}");
        }
    });

    private void OnHotkeyPressed(int id)
    {
        if (id > 0 && id == _toggleHotkeyId)
            _scheduler.Post(() => _ = ToggleAsync());
        else if (id > 0 && id == _muteHotkeyId)
            _scheduler.Post(ToggleMute);
        else if (id > 0 && id == _injectTextHotkeyId)
            _scheduler.Post(() =>
            {
                if (!string.IsNullOrEmpty(FloatingText))
                    InjectOffUiThread(FloatingText);
            });
    }

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _floatingText = "";

    /// <summary>
    /// Manual keyboard input mode for the transcript field. Off by default
    /// (read-only transcript driven by speech). When on, the field is editable
    /// and typed text is fed to the translator instead of speech results.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranscriptReadOnly))]
    private bool _isManualInputEnabled;

    /// <summary>Transcript field is read-only unless manual input mode is on.</summary>
    public bool IsTranscriptReadOnly => !IsManualInputEnabled;

    /// <summary>
    /// While manual input is on, feed the typed transcript to the translator so
    /// text entered by hand can be translated without dictation. When speech
    /// drives the transcript, PartialResult/FinalResult already feed it.
    /// </summary>
    partial void OnFloatingTextChanged(string value)
    {
        // Typed text continues the translation of what is already there; an emptied
        // field is fed too, so text typed afterwards is picked up from the start.
        if (IsManualInputEnabled && IsTranslationEnabled)
            _translation.Feed(value ?? "");
    }

    partial void OnIsManualInputEnabledChanged(bool value)
    {
        // Switching between speech and typing keeps the translation: the text fed so
        // far is finished (its unfinished tail is translated as final) and whatever is
        // typed or dictated next is appended after it. Resetting here re-translated the
        // whole transcript from the beginning.
        if (IsTranslationEnabled)
            _ = _translation.FlushAsync();

        // Leaving manual input: dictation continues after the (possibly edited) text,
        // speech recognized while typing is not pasted in. Entering it keeps the text,
        // and the next recording appends to it instead of starting a clean field.
        _transcript.SetManualInput(value);
    }

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isCaptureMuted;

    [ObservableProperty]
    private bool _isModelLoading;

    [ObservableProperty]
    private bool _isModelDownloading;

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    private string _modelStatusText = "No model loaded";

    /// <summary>
    /// Friendly name of the model the app is configured with: the catalog title
    /// ("Parakeet TDT 0.6B v3 — 4-bit (INT4)") when the folder is a known variant,
    /// otherwise the raw folder name.
    /// </summary>
    public string ModelDisplayName => ModelCatalog.DescribeFolder(Settings.SelectedModel);

    [ObservableProperty]
    private double _downloadProgress;

    // ── Download queue (parallel downloads, aggregate progress) ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueProgressVisible))]
    private bool _isQueueActive;

    [ObservableProperty]
    private double _queueProgressPercent;

    [ObservableProperty]
    private string _queueProgressText = "";

    public bool IsQueueProgressVisible => IsQueueActive;

    // ── Model availability banners ──────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsrModelBannerVisible))]
    private bool _isAsrModelMissing;

    [ObservableProperty]
    private string _asrModelBannerText = "ASR model is not available — download it to start dictation.";

    public bool IsAsrModelBannerVisible => IsAsrModelMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslationModelBannerVisible))]
    private bool _isTranslationModelMissing;

    [ObservableProperty]
    private string _translationModelBannerText =
        "Translation model is not downloaded — translation will use the HTTP server fallback or stay off.";

    [ObservableProperty]
    private bool _isAsrModelPartial;

    [ObservableProperty]
    private bool _isTranslationModelPartial;

    public bool IsTranslationModelBannerVisible => IsTranslationModelMissing && IsTranslationEnabled;

    private void RefreshQueueProgress()
    {
        var progress = DownloadProgressFormatter.DescribeQueue(_downloads.Totals);
        IsQueueActive = progress.IsActive;
        QueueProgressPercent = progress.Percent;
        QueueProgressText = progress.Text;
    }

    /// <summary>Mirrors the running download into the model status line.</summary>
    private void ReportModelDownloadProgress(DownloadJob job)
    {
        var status = DownloadProgressFormatter.DescribeJob(job);
        if (status is null)
            return;

        DownloadProgress = job.Percent;
        ModelStatusText = status;
        OnPropertyChanged(nameof(RecordButtonText));
    }

    private void RefreshModelBanners()
    {
        // Check the configured/asr path integrity so a broken or partially
        // downloaded model is reported as "re-download" rather than "missing".
        var asrPath = ModelPathResolver.FindExistingModelPath(Settings);
        var asrIntegrity = asrPath is not null
            ? ModelPathResolver.ModelIntegrity.Complete
            : ModelPathResolver.CheckIntegrity(Settings.ModelPath);
        IsAsrModelPartial = asrIntegrity == ModelPathResolver.ModelIntegrity.Broken;
        IsAsrModelMissing = asrIntegrity != ModelPathResolver.ModelIntegrity.Complete;
        AsrModelBannerText = asrIntegrity switch
        {
            ModelPathResolver.ModelIntegrity.Broken =>
                "ASR model is broken or incomplete — re-download it to start dictation.",
            ModelPathResolver.ModelIntegrity.Missing =>
                "ASR model is not available — download it to start dictation.",
            _ => AsrModelBannerText
        };

        IsTranslationModelPartial = !TranslationModelInfo.IsDownloaded && TranslationModelInfo.HasPartialDownload;
        IsTranslationModelMissing = !TranslationModelInfo.IsDownloaded;
        TranslationModelBannerText = IsTranslationModelPartial
            ? "Translation model download is incomplete — re-download it to finish."
            : "Translation model is not downloaded — translation will use the HTTP server fallback or stay off.";

        OnPropertyChanged(nameof(IsTranslationModelBannerVisible));
    }

    /// <summary>Enqueues the ASR model download into the shared queue (force re-download when partial).</summary>
    public void EnqueueAsrModelDownload()
    {
        var modelsRoot = string.IsNullOrWhiteSpace(Settings.ModelsRootPath)
            ? AppPaths.ModelsDir
            : Settings.ModelsRootPath;

        if (IsAsrModelPartial)
            DeleteModelFolder(DefaultAsrModel.FolderPath(modelsRoot));

        var job = _downloads.Enqueue(DefaultAsrModel.CreateRequest(modelsRoot));
        _ = job.Completion.ContinueWith(
            _ => _scheduler.Post(() => _ = AdoptDownloadedModelAsync(job, modelsRoot)),
            TaskScheduler.Default);
    }

    /// <summary>
    /// Points the app at a freshly downloaded model. Runs on the UI thread and hands the
    /// change to the settings store, so it cannot be lost to a concurrent write.
    /// </summary>
    private async Task AdoptDownloadedModelAsync(DownloadJob job, string modelsRoot)
    {
        if (job.State != DownloadJobState.Completed || job.ResultPath is null)
            return;

        try
        {
            await _settingsStore.MutateAsync(s =>
            {
                s.ModelsRootPath = modelsRoot;
                s.SelectedModel = Path.GetFileName(job.ResultPath);
                s.ModelPath = job.ResultPath;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _scheduler.Post(() => StatusText = $"Settings error: {ex.Message}");
            return;
        }

        _scheduler.Post(RefreshModelBanners);
    }

    /// <summary>Enqueues the translation model download into the shared queue (force re-download when partial).</summary>
    public void EnqueueTranslationModelDownload()
    {
        if (IsTranslationModelPartial)
            TranslationModelInfo.DeleteDownloaded();

        var job = _downloads.Enqueue(TranslationModelInfo.CreateRequest());
        _ = job.Completion.ContinueWith(
            _ => _scheduler.Post(() =>
            {
                RefreshModelBanners();
                if (job.State == DownloadJobState.Completed)
                    _translation.UpdateBackend(TranslationService.BackendKind.Native);
            }),
            TaskScheduler.Default);
    }

    /// <summary>Removes an installed model folder (repair of a broken download).</summary>
    private static void DeleteModelFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — the download reports the real problem.
        }
    }

    [ObservableProperty]
    private bool _isTextInjectionEnabled;

    [ObservableProperty]
    private bool _isAutoScrollEnabled;

    [ObservableProperty]
    private string _selectedLanguage;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslationVisible))]
    private bool _isTranslationEnabled;

    [ObservableProperty]
    private string _translatedText = "";

    [ObservableProperty]
    private string _translationStatusText = "Translation off";

    [ObservableProperty]
    private string _translationTargetLanguage = "ru";

    public bool IsTranslationVisible => IsTranslationEnabled;

    partial void OnIsTranslationEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTranslationModelBannerVisible));
        if (_isProjectingSettingsSnapshot)
            return;

        Persist(s => s.TranslationEnabled = value);
        if (!value)
        {
            _translation.Reset();
            TranslatedText = "";
            return;
        }

        // Translate from now on only; "Translate all" covers the text already on screen.
        _translation.StartFrom(FloatingText ?? "");

        // Warm up the translation engine in the background as soon as it's enabled.
        _ = _translation.EnsureConnectedAsync();
    }

    partial void OnTranslationTargetLanguageChanged(string value)
    {
        if (_isProjectingSettingsSnapshot)
            return;

        Persist(s => s.TranslationTargetLanguage = value);
        _translation.SetTargetLanguage(value);
    }

    public IReadOnlyList<string> LanguageOptions => SettingsViewModel.DefaultLanguageOptions;

    /// <summary>Translation targets: real languages only ("auto" is a recognition option, not a target).</summary>
    public IReadOnlyList<string> TranslationLanguageOptions { get; } =
        SpeechLib.Translation.TranslationLanguages.All.Select(l => l.Code).ToList();

    public string RecordButtonText => IsModelDownloading
        ? "Downloading model..."
        : IsModelLoading
        ? "Loading model..."
        : IsRecording ? "Stop" : "Start";

    public string RecordingIndicator => IsRecording
        ? (IsCaptureMuted ? "Muted" : "Recording...")
        : "Idle";

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsRecording && IsCaptureMuted)
        {
            _recognition.SetMuted(false);
            IsCaptureMuted = false;
            StatusText = "Listening...";
            return;
        }

        if (IsRecording)
        {
            _recognition.Stop();
            return;
        }

        if (IsModelLoading || IsModelDownloading)
            return;

        if (_recognition.ModelState != ModelLifecycleState.Loaded)
        {
            try
            {
                await EnsureModelReadyAsync();
            }
            catch (Exception ex)
            {
                StatusText = $"Model load error: {ex.Message}";
                return;
            }
        }

        // A new recording starts with an empty field only when the setting asks for it
        // and the user has not been in manual input since the last recording; text typed
        // or kept in manual mode stays and the new speech is appended after it. The
        // coordinator clears the field through TextChanged, which also feeds the
        // translator with the emptied text.
        _transcript.BeginRecordingSession(Settings.ClearTextOnModelOrSessionChange);

        try
        {
            await _recognition.StartAsync(Settings);
            IsRecording = true;
            StatusText = "Listening...";
            _tray.SetRecording(true);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            IsRecording = false;
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (!IsRecording) return;
        var muted = !IsCaptureMuted;
        _recognition.SetMuted(muted);
        IsCaptureMuted = muted;
        StatusText = muted ? "Muted (audio discarded)" : "Listening...";
        OnPropertyChanged(nameof(RecordingIndicator));
    }

    /// <summary>Translates the whole current transcript (live translation otherwise starts when it is switched on).</summary>
    [RelayCommand]
    private async Task TranslateAll()
    {
        if (!IsTranslationEnabled || string.IsNullOrWhiteSpace(FloatingText))
            return;

        try
        {
            await _translation.TranslateAllAsync(FloatingText);
        }
        catch (Exception ex)
        {
            TranslationStatusText = $"Translation error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Copy()
    {
        if (!string.IsNullOrEmpty(FloatingText))
            _textInjector.CopyToClipboard(FloatingText);
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(RecordingIndicator));
    }

    partial void OnIsCaptureMutedChanged(bool value) =>
        OnPropertyChanged(nameof(RecordingIndicator));

    partial void OnIsModelLoadingChanged(bool value) =>
        OnPropertyChanged(nameof(RecordButtonText));

    partial void OnIsModelDownloadingChanged(bool value) =>
        OnPropertyChanged(nameof(RecordButtonText));

    partial void OnIsTextInjectionEnabledChanged(bool value) =>
        Persist(s => s.IsTextInjectionEnabled = value);

    partial void OnIsAutoScrollEnabledChanged(bool value) =>
        Persist(s => s.IsAutoScrollEnabled = value);

    partial void OnAlwaysOnTopChanged(bool value) =>
        Persist(s => s.AlwaysOnTop = value);

    partial void OnSelectedLanguageChanged(string value)
    {
        if (_isProjectingSettingsSnapshot)
            return;

        Persist(s => s.Language = value);
        _translation.SetSourceLanguage(value);
        if (_recognition.ModelState == ModelLifecycleState.Loaded)
            _ = Task.Run(() => _recognition.SetLanguage(value));
    }

    public AppSettings CreateSettingsSnapshot() => _settingsStore.Snapshot();

    public async Task ApplySettingsAsync(AppSettings newSettings)
    {
        var version = Interlocked.Increment(ref _settingsApplyVersion);
        await _settingsApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (version != Volatile.Read(ref _settingsApplyVersion))
                return;

            var previousSettings = Settings;
            ModelPathResolver.ApplyExistingModelPath(newSettings);
            await _settingsStore.ReplaceAsync(newSettings).ConfigureAwait(false);

            // The snapshot mutates observable properties and the banners read the same
            // state, so both run on the UI thread.
            _scheduler.Post(() =>
            {
                ProjectSettingsSnapshot(newSettings);
                RefreshModelBanners();
                OnPropertyChanged(nameof(ModelDisplayName));
            });
            _ = ReregisterHotkeysAsync();

            var previousModelPath = _recognition.LoadedModelPath;
            var newModelPath = ModelPathResolver.FindExistingModelPath(newSettings) ?? newSettings.ModelPath;
            var modelChanged = !PathsEqual(previousModelPath, newModelPath);
            var audioSourceChanged = !string.Equals(
                previousSettings.AudioSource,
                newSettings.AudioSource,
                StringComparison.OrdinalIgnoreCase);

            if (modelChanged)
            {
                await ReloadModelAsync(newSettings, newModelPath, version).ConfigureAwait(false);
                return;
            }

            await Task.Run(() => _recognition.ApplyRuntimeSettings(newSettings)).ConfigureAwait(false);
            if (audioSourceChanged && _recognition.IsRunning)
                await RestartCaptureAsync(newSettings, version).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _scheduler.Post(() => StatusText = $"Settings error: {ex.Message}");
        }
        finally
        {
            _settingsApplyGate.Release();
        }
    }

    private async Task InitializeModelAsync()
    {
        try
        {
            await EnsureModelReadyAsync().ConfigureAwait(false);
            if (!Settings.FirstRunCompleted)
                await _settingsStore.MutateAsync(s => s.FirstRunCompleted = true).ConfigureAwait(false);

            _scheduler.Post(() =>
            {
                IsModelLoading = false;
                IsModelDownloading = false;
                ModelStatusText = "Model ready";
                if (Settings.AutoStartRecognition)
                    _ = ToggleAsync();
            });
        }
        catch (Exception ex)
        {
            _scheduler.Post(() =>
            {
                IsModelLoading = false;
                IsModelDownloading = false;
                ModelStatusText = $"Model unavailable: {ex.Message}";
                StatusText = "Ready - download or select a model in Settings";
            });
        }
    }

    private async Task EnsureModelReadyAsync()
    {
        var modelPath = ModelPathResolver.FindExistingModelPath(Settings);
        if (modelPath is null)
        {
            SetModelPreparationState(true, true, "Downloading model...");
            var modelsRoot = string.IsNullOrWhiteSpace(Settings.ModelsRootPath)
                ? AppPaths.ModelsDir
                : Settings.ModelsRootPath;

            // Enqueue into the shared download manager and await this job's
            // completion. Aggregate progress shows on the main window.
            var job = _downloads.Enqueue(DefaultAsrModel.CreateRequest(modelsRoot));
            modelPath = await job.Completion.ConfigureAwait(false);

            await _settingsStore.MutateAsync(s =>
            {
                s.ModelsRootPath = modelsRoot;
                s.SelectedModel = Path.GetFileName(modelPath);
                s.ModelPath = modelPath;
            }).ConfigureAwait(false);
        }
        else if (ModelPathResolver.ApplyExistingModelPath(Settings))
        {
            // The discovered path differed from the stored one; persist it so the next
            // start is a plain read.
            await _settingsStore.SaveAsync().ConfigureAwait(false);
        }

        _scheduler.Post(RefreshModelBanners);
        SetModelPreparationState(true, false, "Loading model...");
        await _recognition.LoadModelAsync(Settings).ConfigureAwait(false);
        if (_recognition.ModelState != ModelLifecycleState.Loaded)
            throw new InvalidOperationException("The speech model did not reach the Loaded state.");
    }

    private async Task ReloadModelAsync(AppSettings settings, string? modelPath, int version)
    {
        _scheduler.Post(() =>
        {
            IsModelReady = false;
            IsModelLoading = true;
            ModelStatusText = "Reloading model...";
        });

        await _recognition.StopAndCleanupAsync().ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        _scheduler.Post(() => IsRecording = false);
        _recognition.UnloadModel();
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            _scheduler.Post(() =>
            {
                IsModelLoading = false;
                ModelStatusText = "No model selected";
            });
            return;
        }

        settings.ModelPath = modelPath;
        await _recognition.LoadModelAsync(settings).ConfigureAwait(false);
        // The recognizer call takes the operation gate; keep it off the calling thread.
        await Task.Run(() => _recognition.ApplyRuntimeSettings(settings)).ConfigureAwait(false);
    }

    private async Task RestartCaptureAsync(AppSettings settings, int version)
    {
        var wasRecording = _recognition.IsRunning;
        await _recognition.StopAndCleanupAsync().ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        _scheduler.Post(() =>
        {
            IsRecording = false;
            IsCaptureMuted = false;
        });

        if (!wasRecording)
            return;

        // The restarted recognizer begins a new session text; queued before Start so it
        // runs ahead of the first result, which is dispatched the same way.
        _scheduler.Post(_transcript.StartNewSpeechSegment);
        await _recognition.StartAsync(settings);
        _scheduler.Post(() =>
        {
            IsRecording = true;
            StatusText = "Listening...";
            _tray.SetRecording(true);
        });
    }

    /// <summary>
    /// Writes a whole settings snapshot onto the observable properties. Runs on the UI
    /// thread; the per-property persistence handlers are suppressed because the snapshot
    /// path persists once (see <see cref="_isProjectingSettingsSnapshot"/>).
    /// </summary>
    private void ProjectSettingsSnapshot(AppSettings settings)
    {
        _isProjectingSettingsSnapshot = true;
        try
        {
            if (!string.Equals(SelectedLanguage, settings.Language, StringComparison.Ordinal))
                SelectedLanguage = settings.Language;
            IsTextInjectionEnabled = settings.IsTextInjectionEnabled;
            IsAutoScrollEnabled = settings.IsAutoScrollEnabled;
            AlwaysOnTop = settings.AlwaysOnTop;
            IsTranslationEnabled = settings.TranslationEnabled;
            TranslationTargetLanguage = settings.TranslationTargetLanguage;
            _translation.SetTargetLanguage(settings.TranslationTargetLanguage);
            _translation.SetSourceLanguage(settings.Language);
            _translation.SetAdditionalSystemPrompt(settings.TranslationSystemPrompt);
            _translation.UpdateServerUrl(settings.TranslationServerUrl);
            _translation.UpdateBackend(string.Equals(settings.TranslationBackend, "http", StringComparison.OrdinalIgnoreCase)
                ? TranslationService.BackendKind.Http
                : TranslationService.BackendKind.Native);
            _translation.SetComputeBackend(settings.TranslationComputeBackend);
            if (settings.TranslationEnabled)
                _ = _translation.EnsureConnectedAsync();

            if (_textInjector is LinuxTextInjector linuxInjector
                && !string.IsNullOrWhiteSpace(settings.PasteChord))
                linuxInjector.PasteChord = settings.PasteChord.Trim();
        }
        finally
        {
            _isProjectingSettingsSnapshot = false;
        }
    }

    private void SetModelPreparationState(bool loading, bool downloading, string status)
    {
        _scheduler.Post(() =>
        {
            IsModelLoading = loading;
            IsModelDownloading = downloading;
            ModelStatusText = status;
        });
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
