using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using VoiceType.Hotkeys;
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
    private readonly SettingsService _settingsService;
    private readonly ModelDownloadService _modelDownloader;
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IPlatformTextInjector _textInjector;
    private readonly ITrayIndicator _tray;
    private readonly TranslationService _translation;
    private readonly DispatcherQueue _dispatcher;

    private AppSettings _settings;
    private int _toggleHotkeyId;
    private readonly SemaphoreSlim _settingsApplyGate = new(1, 1);
    private int _settingsApplyVersion;
    private Task? _modelInitializationTask;
    private bool _isApplyingSettingsSnapshot;

    public MainViewModel(
        RecognitionService recognition,
        SettingsService settingsService,
        ModelDownloadService modelDownloader,
        IGlobalHotkeyService hotkeys,
        IPlatformTextInjector textInjector,
        ITrayIndicator tray,
        TranslationService translation)
    {
        _recognition = recognition;
        _settingsService = settingsService;
        _modelDownloader = modelDownloader;
        _hotkeys = hotkeys;
        _textInjector = textInjector;
        _tray = tray;
        _translation = translation;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _settings = settingsService.Load();
        _selectedLanguage = _settings.Language;
        IsTextInjectionEnabled = _settings.IsTextInjectionEnabled;
        IsAutoScrollEnabled = _settings.IsAutoScrollEnabled;
        AlwaysOnTop = _settings.AlwaysOnTop;
        IsTranslationEnabled = _settings.TranslationEnabled;
        _translation.SetTargetLanguage(_settings.TranslationTargetLanguage);

        _recognition.PartialResult += text => _dispatcher.TryEnqueue(() =>
        {
            FloatingText = text;
            if (IsTranslationEnabled)
                _translation.Feed(text);
        });
        _recognition.FinalResult += text => _dispatcher.TryEnqueue(() =>
        {
            FloatingText = text;
            if (IsTextInjectionEnabled && !string.IsNullOrEmpty(text))
                _textInjector.Inject(text);

            if (IsTranslationEnabled)
            {
                _translation.Feed(text);
                _ = _translation.FlushAsync();
            }
        });
        _recognition.Stopped += () => _dispatcher.TryEnqueue(() =>
        {
            IsRecording = false;
            StatusText = "Ready";
            _tray.SetRecording(false);
        });
        _recognition.ModelStateChanged += state => _dispatcher.TryEnqueue(() =>
        {
            IsModelLoading = state == ModelLifecycleState.Loading;
            IsModelReady = state == ModelLifecycleState.Loaded;
            ModelStatusText = state switch
            {
                ModelLifecycleState.Unloaded => "No model loaded",
                ModelLifecycleState.Loading => "Loading model...",
                ModelLifecycleState.Loaded => "Model ready",
                ModelLifecycleState.Error => "Model load error",
                _ => ""
            };
            OnPropertyChanged(nameof(RecordButtonText));
        });
        _recognition.Error += exception => _dispatcher.TryEnqueue(() =>
            StatusText = $"Recognition error: {exception.Message}");

        _modelDownloader.ProgressChanged += progress => _dispatcher.TryEnqueue(() =>
        {
            DownloadProgress = progress.OverallProgress;
            ModelStatusText = progress.TotalFiles > 0
                ? $"Downloading model... {progress.OverallProgress:F0}% ({progress.DownloadedFiles}/{progress.TotalFiles})"
                : "Downloading model...";
            OnPropertyChanged(nameof(RecordButtonText));
        });

        // Global hotkeys: portal grants bindings asynchronously (consent dialog
        // on first run). Registration happens in the background; presses arrive
        // via the HotkeyPressed event.
        _hotkeys.HotkeyPressed += id =>
        {
            if (id == _toggleHotkeyId)
                _dispatcher.TryEnqueue(() => _ = ToggleAsync());
        };

        // Tray indicator: register with the desktop environment; activation
        // (icon click) toggles recording like the main button.
        _tray.Activated += () => _dispatcher.TryEnqueue(() => _ = ToggleAsync());
        _ = _tray.InitializeAsync();

        // Live translation: stream transcript deltas through the LiteRT-LM
        // server; translated text is displayed (and injectable) alongside the
        // original transcript.
        _translation.TranslationChanged += text => _dispatcher.TryEnqueue(() => TranslatedText = text);
        _translation.StatusChanged += status => _dispatcher.TryEnqueue(() => TranslationStatusText = status);

        if (_hotkeys.IsAvailable && !string.IsNullOrWhiteSpace(_settings.ToggleHotkey))
            _ = RegisterToggleHotkeyAsync(_settings.ToggleHotkey);

        IsModelLoading = true;
        ModelStatusText = "Checking model...";
        _modelInitializationTask = InitializeModelAsync();
    }

    private async Task RegisterToggleHotkeyAsync(string chord)
    {
        var id = await _hotkeys.RegisterAsync(chord);
        if (id > 0)
            _toggleHotkeyId = id;
    }

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _floatingText = "";

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

    [ObservableProperty]
    private double _downloadProgress;

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

    public bool IsTranslationVisible => IsTranslationEnabled;

    partial void OnIsTranslationEnabledChanged(bool value)
    {
        if (_isApplyingSettingsSnapshot)
            return;

        _settings.TranslationEnabled = value;
        _ = Task.Run(() => _settingsService.Update(s => s.TranslationEnabled = value));
        if (!value)
        {
            _translation.Reset();
            TranslatedText = "";
        }
    }

    public IReadOnlyList<string> LanguageOptions => SettingsViewModel.DefaultLanguageOptions;

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

        try
        {
            _recognition.Start(_settings);
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
        _ = Task.Run(() => _settingsService.Update(s => s.IsTextInjectionEnabled = value));

    partial void OnIsAutoScrollEnabledChanged(bool value) =>
        _ = Task.Run(() => _settingsService.Update(s => s.IsAutoScrollEnabled = value));

    partial void OnAlwaysOnTopChanged(bool value) =>
        _ = Task.Run(() => _settingsService.Update(s => s.AlwaysOnTop = value));

    partial void OnSelectedLanguageChanged(string value)
    {
        if (_isApplyingSettingsSnapshot)
            return;

        _settings.Language = value;
        _ = Task.Run(() => _settingsService.Update(s => s.Language = value));
        if (_recognition.ModelState == ModelLifecycleState.Loaded)
            _ = Task.Run(() => _recognition.SetLanguage(value));
    }

            public AppSettings CreateSettingsSnapshot() => _settings.Clone();

    public async Task ApplySettingsAsync(AppSettings newSettings)
    {
        var version = Interlocked.Increment(ref _settingsApplyVersion);
        await _settingsApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (version != Volatile.Read(ref _settingsApplyVersion))
                return;

            var previousSettings = _settings;
            ModelPathResolver.ApplyExistingModelPath(newSettings);
            await Task.Run(() => _settingsService.Save(newSettings)).ConfigureAwait(false);
            _settings = newSettings;
            ApplySettingsSnapshot(newSettings);

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
            _dispatcher.TryEnqueue(() => StatusText = $"Settings error: {ex.Message}");
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
            if (!_settings.FirstRunCompleted)
            {
                _settings.FirstRunCompleted = true;
                await Task.Run(() => _settingsService.Save(_settings)).ConfigureAwait(false);
            }

            _dispatcher.TryEnqueue(() =>
            {
                IsModelLoading = false;
                IsModelDownloading = false;
                ModelStatusText = "Model ready";
                if (_settings.AutoStartRecognition)
                    _ = ToggleAsync();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
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
        var settings = _settingsService.Load();
        var modelPath = ModelPathResolver.FindExistingModelPath(settings);
        if (modelPath is null)
        {
            SetModelPreparationState(true, true, "Downloading model...");
            var modelsRoot = string.IsNullOrWhiteSpace(settings.ModelsRootPath)
                ? AppPaths.ModelsDir
                : settings.ModelsRootPath;
            modelPath = await _modelDownloader.DownloadRecommendedAsync(modelsRoot).ConfigureAwait(false);
            settings.ModelsRootPath = modelsRoot;
            settings.SelectedModel = Path.GetFileName(modelPath);
            settings.ModelPath = modelPath;
            await Task.Run(() => _settingsService.Save(settings)).ConfigureAwait(false);
        }
        else
        {
            ModelPathResolver.ApplyExistingModelPath(settings);
        }

        _settings = settings;
        SetModelPreparationState(true, false, "Loading model...");
        await _recognition.LoadModelAsync(settings).ConfigureAwait(false);
        if (_recognition.ModelState != ModelLifecycleState.Loaded)
            throw new InvalidOperationException("The speech model did not reach the Loaded state.");
    }

    private async Task ReloadModelAsync(AppSettings settings, string? modelPath, int version)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsModelReady = false;
            IsModelLoading = true;
            ModelStatusText = "Reloading model...";
        });

        await _recognition.StopAndCleanupAsync().ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        _dispatcher.TryEnqueue(() => IsRecording = false);
        _recognition.UnloadModel();
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            _dispatcher.TryEnqueue(() =>
            {
                IsModelLoading = false;
                ModelStatusText = "No model selected";
            });
            return;
        }

        settings.ModelPath = modelPath;
        await _recognition.LoadModelAsync(settings).ConfigureAwait(false);
        await Task.Run(() => _recognition.ApplyRuntimeSettings(settings)).ConfigureAwait(false);
    }

    private async Task RestartCaptureAsync(AppSettings settings, int version)
    {
        var wasRecording = _recognition.IsRunning;
        await _recognition.StopAndCleanupAsync().ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        _dispatcher.TryEnqueue(() =>
        {
            IsRecording = false;
            IsCaptureMuted = false;
        });

        if (!wasRecording)
            return;

        _recognition.Start(settings);
        _dispatcher.TryEnqueue(() =>
        {
            IsRecording = true;
            StatusText = "Listening...";
            _tray.SetRecording(true);
        });
    }

    private void ApplySettingsSnapshot(AppSettings settings)
    {
        _isApplyingSettingsSnapshot = true;
        try
        {
            if (!string.Equals(SelectedLanguage, settings.Language, StringComparison.Ordinal))
                SelectedLanguage = settings.Language;
            IsTextInjectionEnabled = settings.IsTextInjectionEnabled;
            IsAutoScrollEnabled = settings.IsAutoScrollEnabled;
            AlwaysOnTop = settings.AlwaysOnTop;
            IsTranslationEnabled = settings.TranslationEnabled;
            _translation.SetTargetLanguage(settings.TranslationTargetLanguage);
            _translation.UpdateServerUrl(settings.TranslationServerUrl);

            if (_textInjector is LinuxTextInjector linuxInjector
                && !string.IsNullOrWhiteSpace(settings.PasteChord))
                linuxInjector.PasteChord = settings.PasteChord.Trim();
        }
        finally
        {
            _isApplyingSettingsSnapshot = false;
        }
    }

    private void SetModelPreparationState(bool loading, bool downloading, string status)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsModelLoading = loading;
            IsModelDownloading = downloading;
            ModelStatusText = status;
        });
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
