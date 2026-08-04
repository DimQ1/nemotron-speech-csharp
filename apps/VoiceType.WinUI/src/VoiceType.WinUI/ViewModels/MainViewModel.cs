using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using SpeechLib.Recognition;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IRecognitionService _recognition;
    private readonly IGlobalInputHook _hook;
    private readonly ITextInjector _textInjector;
    private readonly ISettingsService _settingsService;
    private readonly ISessionManager _sessionManager;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly IPostProcessingPipeline _postProcessing;
    private readonly IWindowInterop _windowInterop;
    private readonly DispatcherQueue _dispatcher;
    private readonly RecognitionStateMachine _stateMachine = new();
    private readonly DispatcherQueueTimer _partialResultTimer;
    private readonly object _partialResultGate = new();

    private AppSettings _settings;
    private int _lastInjectedLength;
    private string _lastInjectedTextTail = ""; // last ~20 chars injected, for punctuation-aware delta
    private int _toggleHotkeyId;
    private int _muteHotkeyId;
    private int _injectTextHotkeyId;
    private nint _injectionTargetWindow;
    private RecognitionSession? _currentSession;
    private Views.SettingsWindow? _settingsWindow;
    private string? _pendingPartialText;
    private bool _hasPendingPartial;
    private bool _injectionExplicitlyEnabled;
    private bool _modelWarningDismissed;
    private bool _isApplyingLanguageMessage;
    private int _languagePersistenceVersion;
    private int _languageApplyVersion;
    private readonly object _languagePersistenceGate = new();
    private readonly object _languageApplyGate = new();
    private readonly SemaphoreSlim _settingsApplyGate = new(1, 1);

    // ---- Observable properties (source-generated) ----

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _recognizedText = "";

    [ObservableProperty]
    private string _floatingText = "";

    private string _preservedText = "";
    private string _currentSessionText = "";
    private int _settingsApplyVersion;
    private bool _isApplyingSettingsSnapshot;

    [ObservableProperty]
    private bool _isTextInjectionEnabled;

    [ObservableProperty]
    private bool _isAutoScrollEnabled;

    [ObservableProperty]
    private bool _disableInjectionOnFocusChange;

    [ObservableProperty]
    private bool _isCaptureMuted;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isModelLoading;

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    private ModelState _modelStateDisplay = ModelState.Unloaded;

    [ObservableProperty]
    private bool _isModelAvailable;

    [ObservableProperty]
    private string _modelStatusText = "No model loaded";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelWarning))]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _isActivelyInjecting;

    [ObservableProperty]
    private string _selectedLanguage = "auto";

    public nint MainWindowHandle { get; set; }

    // ---- Computed properties ----

    public string RecordButtonText => IsModelLoading
        ? "Loading model..."
        : IsRecording ? "Stop" : "Start";

    public string RecordingIndicator => IsRecording
        ? (IsCaptureMuted ? "Muted" : "Recording...")
        : "Idle";

    public bool ShowModelWarning => !IsModelAvailable && !_modelWarningDismissed;

    public static string RecommendedModelRepo => "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu";
    public static string RecommendedModelDisplay => "CPU (INT4, opset24, 0.56s) -- fast, low latency, ~749 MB";

    // ---- Events ----

    public event Action<bool>? AlwaysOnTopChanged;

    // ---- Constructor ----

    public MainViewModel(
        IRecognitionService recognition,
        IGlobalInputHook hook,
        ITextInjector textInjector,
        ISettingsService settingsService,
        ISessionManager sessionManager,
        IGlobalHotkeyService hotkeyService,
        IPostProcessingPipeline postProcessing,
        IWindowInterop windowInterop,
        DispatcherQueue dispatcher)
    {
        _recognition = recognition;
        _hook = hook;
        _textInjector = textInjector;
        _settingsService = settingsService;
        _sessionManager = sessionManager;
        _hotkeyService = hotkeyService;
        _postProcessing = postProcessing;
        _windowInterop = windowInterop;
        _dispatcher = dispatcher;
        _settings = settingsService.Load();
        _selectedLanguage = _settings.Language;

        IsTextInjectionEnabled = _settings.IsTextInjectionEnabled;
        IsAutoScrollEnabled = _settings.IsAutoScrollEnabled;
        DisableInjectionOnFocusChange = _settings.DisableInjectionOnFocusChange;
        AlwaysOnTop = _settings.AlwaysOnTop;

        _hook.InputDetected += OnInputDetected;
        _recognition.PartialResult += OnPartialResult;
        _recognition.FinalResult += OnFinalResult;
        _recognition.Stopped += OnRecognitionStopped;
        _recognition.ModelStateChanged += OnModelStateChanged;

        _partialResultTimer = _dispatcher.CreateTimer();
        _partialResultTimer.Interval = TimeSpan.FromMilliseconds(200);
        _partialResultTimer.Tick += (_, _) => FlushPendingPartialResult();

        // Listen for ModelDownloaded messages
        WeakReferenceMessenger.Default.Register<ModelDownloadedMessage>(this, (r, m) =>
        {
            var newSettings = _settings.Clone();
            newSettings.ModelsRootPath = m.Value.ModelsRootPath;
            newSettings.ModelPath = m.Value.ModelPath;
            ModelPathResolver.ApplyExistingModelPath(newSettings);
            _ = Task.Run(() => _settingsService.Update(settings =>
            {
                settings.ModelsRootPath = newSettings.ModelsRootPath;
                settings.SelectedModel = newSettings.SelectedModel;
                settings.ModelPath = newSettings.ModelPath;
            }));
            _dispatcher.TryEnqueue(() => HandleSettingsSnapshot(newSettings));
        });

        // Listen for SettingsSaved messages — apply the fresh snapshot once, then
        // process runtime settings and model/capture lifecycle work in the background.
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (r, m) =>
        {
            _dispatcher.TryEnqueue(() => HandleSettingsSnapshot(m.Value));
        });

        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            if (string.Equals(SelectedLanguage, m.Value, StringComparison.Ordinal))
                return;

            _dispatcher.TryEnqueue(() =>
            {
                if (string.Equals(SelectedLanguage, m.Value, StringComparison.Ordinal))
                    return;

                _isApplyingLanguageMessage = true;
                try
                {
                    SelectedLanguage = m.Value;
                }
                finally
                {
                    _isApplyingLanguageMessage = false;
                }

                ApplyLanguageSelection(m.Value);
            });
        });

        CheckModelAvailability();

        // Preload model at startup (background, non-blocking)
        _ = LoadModelInBackgroundAsync();
    }

    // ---- Property change hooks ----

    partial void OnIsTextInjectionEnabledChanged(bool value)
    {
        _lastInjectedLength = _currentSessionText.Length;
        if (_isApplyingSettingsSnapshot)
        {
            IsActivelyInjecting = value && IsRecording;
            return;
        }

        _settings.IsTextInjectionEnabled = value;
        SaveSettingsInBackground(settings => settings.IsTextInjectionEnabled = value);

        if (value)
        {
            var foregroundWindow = _windowInterop.GetForegroundWindow();
            var ownWindow = _windowInterop.GetOwnWindowHandle();
            // Don't set injection target to our own window
            _injectionTargetWindow = (ownWindow != nint.Zero && foregroundWindow == ownWindow)
                ? nint.Zero
                : foregroundWindow;
            _injectionExplicitlyEnabled = true;

            if (!IsRecording && !IsModelLoading)
                _ = StartAsync();
        }

        IsActivelyInjecting = value && IsRecording;
    }

    partial void OnIsAutoScrollEnabledChanged(bool value)
    {
        if (_isApplyingSettingsSnapshot)
            return;

        _settings.IsAutoScrollEnabled = value;
        SaveSettingsInBackground(settings => settings.IsAutoScrollEnabled = value);
    }

    partial void OnDisableInjectionOnFocusChangeChanged(bool value)
    {
        if (_isApplyingSettingsSnapshot)
            return;

        _settings.DisableInjectionOnFocusChange = value;
        SaveSettingsInBackground(settings => settings.DisableInjectionOnFocusChange = value);
    }

    partial void OnAlwaysOnTopChanged(bool value)
    {
        if (!_isApplyingSettingsSnapshot)
        {
            _settings.AlwaysOnTop = value;
            SaveSettingsInBackground(settings => settings.AlwaysOnTop = value);
        }

        AlwaysOnTopChanged?.Invoke(value);
    }

    partial void OnIsRecordingChanged(bool value)
    {
        IsActivelyInjecting = value && IsTextInjectionEnabled;
        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(RecordingIndicator));
    }

    partial void OnIsCaptureMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingIndicator));
    }

    partial void OnIsModelAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowModelWarning));
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (_isApplyingLanguageMessage || _isApplyingSettingsSnapshot)
            return;

        ApplyLanguageSelection(value);
        WeakReferenceMessenger.Default.Send(new LanguageChangedMessage(value));
    }

    private void ApplyLanguageSelection(string value)
    {
        _settings.Language = value;
        QueueLanguagePersistence(value);

        if (_recognition.ModelState == ModelState.Loaded)
            QueueRecognitionLanguageChange(value);
    }

    private void QueueLanguagePersistence(string value)
    {
        var version = Interlocked.Increment(ref _languagePersistenceVersion);
        _ = Task.Run(() =>
        {
            try
            {
                lock (_languagePersistenceGate)
                {
                    if (version == Volatile.Read(ref _languagePersistenceVersion))
                        _settingsService.SaveLanguage(value);
                }
            }
            catch (Exception ex)
            {
                try { App.Telemetry?.LogError("Settings", $"Language save failed: {ex.Message}"); } catch { }
            }
        });
    }

    private void QueueRecognitionLanguageChange(string value)
    {
        var version = Interlocked.Increment(ref _languageApplyVersion);
        _ = Task.Run(() =>
        {
            try
            {
                lock (_languageApplyGate)
                {
                    if (version == Volatile.Read(ref _languageApplyVersion))
                        _recognition.SetLanguage(value);
                }
            }
            catch (Exception ex)
            {
                try { App.Telemetry?.LogError("Recognition", $"Language change failed: {ex.Message}"); } catch { }
            }
        });
    }

    // ---- Commands ----

    [RelayCommand]
    private void Toggle()
    {
        if (IsRecording && IsCaptureMuted)
        {
            // Paused: resume audio processing
            _recognition.SetMuted(false);
            IsCaptureMuted = false;
            StatusText = "Listening...";
            OnPropertyChanged(nameof(RecordingIndicator));
            return;
        }

        if (IsRecording) Stop();
        else if (!IsModelLoading) _ = StartAsync();
    }

    [RelayCommand]
    private void Copy()
    {
        if (!string.IsNullOrEmpty(FloatingText))
            _textInjector.CopyToClipboard(FloatingText);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // Guard against duplicate windows: the static OpenInstance reference stays alive
        // while the window is open (unlike this VM field, which the GC could collect
        // after Close() but before the Closed handler runs).
        if (Views.SettingsWindow.OpenInstance is { } existing)
        {
            existing.Activate();
            return;
        }

        // Cross-process guard: block a second settings window even from another
        // app instance (e.g. installed MSIX package running alongside a debug build).
        if (!Views.SettingsWindow.TryAcquireGlobalGuard())
            return;

        var settingsWindow = new Views.SettingsWindow(_settings);
        _settingsWindow = settingsWindow;
        App.MainWindow?.TrackChildWindow(settingsWindow);
        settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
        };
        settingsWindow.Activate();
    }

    [RelayCommand]
    private void OpenModelDownloader()
    {
        if (Views.ModelDownloaderWindow.OpenInstance is not null)
        {
            Views.ModelDownloaderWindow.OpenInstance.Activate();
            return;
        }

        if (!Views.ModelDownloaderWindow.TryAcquireGlobalGuard())
            return;

        var window = new Views.ModelDownloaderWindow();
        App.MainWindow?.TrackChildWindow(window);
        window.Closed += (_, _) =>
        {
            if (window.ViewModel.WasDownloaded && window.ViewModel.ResultModelPath is not null)
            {
                var msg = new ModelDownloadedMessage(
                    window.ViewModel.ResultPath ?? _settings.ModelsRootPath,
                    window.ViewModel.ResultModelPath);
                WeakReferenceMessenger.Default.Send(msg);
            }
        };
        window.Activate();
    }

    [RelayCommand]
    private void OpenAudioMixer()
    {
        if (Views.AudioMixerWindow.OpenInstance is { } existing)
        {
            existing.Activate();
            return;
        }

        // Cross-process guard: block a second mixer window even from another app instance.
        if (!Views.AudioMixerWindow.TryAcquireGlobalGuard())
            return;

        var mixerViewModel = new AudioMixerViewModel(_settingsService, _dispatcher);
        var mixerWindow = new Views.AudioMixerWindow(mixerViewModel);
        App.MainWindow?.TrackChildWindow(mixerWindow);
        mixerWindow.Activate();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        var help = Views.HelpWindow.OpenInstance ?? new Views.HelpWindow();
        App.MainWindow?.TrackChildWindow(help);
        help.Activate();
    }

    // ---- Hotkey ----

    public void TryAutoStart()
    {
        if (_settings.AutoStartRecognition && !IsRecording && !IsModelLoading)
            _ = StartAsync();
    }

    public void RegisterHotkey(nint hwnd)
    {
        _hotkeyService.UnregisterAll();
        _toggleHotkeyId = 0;
        _muteHotkeyId = 0;
        _injectTextHotkeyId = 0;

        var toggle = _settings.ToggleHotkey;
        if (!string.IsNullOrEmpty(toggle))
            _toggleHotkeyId = _hotkeyService.Register(hwnd, toggle);

        var mute = _settings.MuteHotkey;
        if (!string.IsNullOrEmpty(mute))
            _muteHotkeyId = _hotkeyService.Register(hwnd, mute);

        var inject = _settings.InjectTextHotkey;
        if (!string.IsNullOrEmpty(inject))
            _injectTextHotkeyId = _hotkeyService.Register(hwnd, inject);
    }

    public bool HandleHotkey(int hotkeyId)
    {
        if (hotkeyId == _toggleHotkeyId && _toggleHotkeyId != 0)
        {
            Toggle();
            return true;
        }
        if (hotkeyId == _muteHotkeyId && _muteHotkeyId != 0)
        {
            ToggleMute();
            return true;
        }
        if (hotkeyId == _injectTextHotkeyId && _injectTextHotkeyId != 0)
        {
            ToggleTextInjection();
            return true;
        }
        return false;
    }

    public void ToggleTextInjection()
    {
        var wasRecording = IsRecording;
        var enable = !IsTextInjectionEnabled;
        IsTextInjectionEnabled = enable;

        if (!enable)
            StatusText = "Text injection disabled";
        else if (wasRecording)
            StatusText = "Text injection enabled";
    }

    public void InjectCurrentText()
    {
        if (!IsTextInjectionEnabled) return;
        if (string.IsNullOrEmpty(FloatingText)) return;
        _textInjector.Inject(FloatingText, _settings.TextInjectionMethod);
    }

    // ---- Model lifecycle ----

    private async Task LoadModelInBackgroundAsync()
    {
        var settings = _settingsService.Load();
        var modelPath = ModelPathResolver.FindExistingModelPath(settings);
        if (modelPath is null)
        {
            _dispatcher.TryEnqueue(() => ModelStatusText = "No model configured — download or select in Settings");
            return;
        }

        if (ModelPathResolver.ApplyExistingModelPath(settings))
        {
            var normalizedSettings = settings.Clone();
            await Task.Run(() => _settingsService.Update(current =>
            {
                current.ModelsRootPath = normalizedSettings.ModelsRootPath;
                current.SelectedModel = normalizedSettings.SelectedModel;
                current.ModelPath = normalizedSettings.ModelPath;
            }));
        }
        _settings = settings;

        // The first-run wizard already loaded the model before revealing the main
        // window — skip a redundant reload (it would just waste startup time).
        if (_recognition.ModelState == ModelState.Loaded)
        {
            _dispatcher.TryEnqueue(() =>
            {
                IsModelReady = true;
                ModelStatusText = "Model ready";
            });
            return;
        }

        await _recognition.LoadModelAsync(settings);
    }

    private void HandleSettingsSnapshot(AppSettings settings)
    {
        var previousAudioSource = _settings.AudioSource;
        ApplySettingsSnapshot(settings);
        CheckModelAvailability();

        if (MainWindowHandle != nint.Zero)
            RegisterHotkey(MainWindowHandle);

        var version = Interlocked.Increment(ref _settingsApplyVersion);
        _ = ApplySettingsAndLifecycleAsync(settings, previousAudioSource, version);
    }

    private async Task ApplySettingsAndLifecycleAsync(AppSettings newSettings, string previousAudioSource, int version)
    {
        await _settingsApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (version != Volatile.Read(ref _settingsApplyVersion))
                return;

            await Task.Run(() => _recognition.ApplyRuntimeSettings(newSettings)).ConfigureAwait(false);

            if (version != Volatile.Read(ref _settingsApplyVersion))
                return;

            var newModelPath = GetModelIdentity(newSettings);
            var loadedModelPath = _recognition.LoadedModelPath;
            if (!PathsEqual(loadedModelPath, newModelPath))
            {
                await ReloadModelOnSettingsChangeAsync(newSettings, version).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(previousAudioSource, newSettings.AudioSource, StringComparison.OrdinalIgnoreCase)
                && _recognition.IsRunning)
            {
                await RestartCaptureAsync(version).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            try { App.Telemetry?.LogError("Settings", $"Runtime settings apply failed: {ex.Message}"); } catch { }
            _dispatcher.TryEnqueue(() => StatusText = $"Settings error: {ex.Message}");
        }
        finally
        {
            _settingsApplyGate.Release();
        }
    }

    private async Task ReloadModelOnSettingsChangeAsync(AppSettings newSettings, int version)
    {
        await RunOnDispatcherAsync(() =>
        {
            ModelStatusText = "Reloading model...";
            IsModelReady = false;
        }).ConfigureAwait(false);

        var wasRunning = _recognition.IsRunning;
        await _recognition.StopAndCleanupAsync().ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        await RunOnDispatcherAsync(() => BeginTextTransition(_settings.ClearTextOnModelOrSessionChange))
            .ConfigureAwait(false);

        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        await Task.Run(_recognition.UnloadModel).ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        await _recognition.LoadModelAsync(newSettings).ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        await Task.Run(() => _recognition.ApplyRuntimeSettings(newSettings)).ConfigureAwait(false);

        if (wasRunning)
        {
            _dispatcher.TryEnqueue(() => StatusText = "Model changed. Press Start to begin a new session.");
        }
    }

    private async Task RestartCaptureAsync(int version)
    {
        await _recognition.StopAndCleanupAsync().ConfigureAwait(false);
        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        await RunOnDispatcherAsync(() => BeginTextTransition(_settings.ClearTextOnModelOrSessionChange))
            .ConfigureAwait(false);

        if (version != Volatile.Read(ref _settingsApplyVersion))
            return;

        _dispatcher.TryEnqueue(() =>
        {
            IsRecording = false;
            IsCaptureMuted = false;
            _ = StartAsync();
        });
    }

    private void BeginTextTransition(bool clearText)
    {
        if (clearText)
            _preservedText = "";
        else if (!string.IsNullOrEmpty(FloatingText))
            _preservedText = FloatingText;

        _currentSessionText = "";
        _lastInjectedLength = 0;
        _lastInjectedTextTail = "";

        lock (_partialResultGate)
        {
            _pendingPartialText = null;
            _hasPendingPartial = false;
        }

        UpdateDisplayedText();
    }

    private void UpdateDisplayedText()
    {
        var displayText = CombineDisplayText(_preservedText, _currentSessionText);
        RecognizedText = displayText;
        FloatingText = displayText;
    }

    private static string CombineDisplayText(string preservedText, string currentSessionText)
    {
        if (string.IsNullOrEmpty(preservedText))
            return currentSessionText;
        if (string.IsNullOrEmpty(currentSessionText))
            return preservedText;
        if (char.IsWhiteSpace(preservedText[^1]) || char.IsWhiteSpace(currentSessionText[0]))
            return preservedText + currentSessionText;

        return preservedText + Environment.NewLine + currentSessionText;
    }

    private void OnModelStateChanged(ModelState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ModelStateDisplay = state;
            IsModelLoading = state == ModelState.Loading;
            IsModelReady = state == ModelState.Loaded;

            ModelStatusText = state switch
            {
                ModelState.Unloaded => "No model loaded",
                ModelState.Loading => "Loading model...",
                ModelState.Loaded => "Model ready",
                ModelState.Error => "Model load error",
                _ => ""
            };

            OnPropertyChanged(nameof(RecordButtonText));
        });
    }

    public void DismissModelWarning()
    {
        _modelWarningDismissed = true;
        OnPropertyChanged(nameof(ShowModelWarning));
    }

    private void CheckModelAvailability()
    {
        IsModelAvailable = ModelPathResolver.FindExistingModelPath(_settings) is not null;

        if (!IsModelAvailable)
            ModelStatusText = "No model found. Download recommended:";
    }

    private void ApplySettingsSnapshot(AppSettings settings)
    {
        _isApplyingSettingsSnapshot = true;
        try
        {
            _settings = settings;

            if (!string.Equals(SelectedLanguage, settings.Language, StringComparison.Ordinal))
                SelectedLanguage = settings.Language;

            IsTextInjectionEnabled = settings.IsTextInjectionEnabled;
            IsAutoScrollEnabled = settings.IsAutoScrollEnabled;
            DisableInjectionOnFocusChange = settings.DisableInjectionOnFocusChange;
            AlwaysOnTop = settings.AlwaysOnTop;
        }
        finally
        {
            _isApplyingSettingsSnapshot = false;
        }
    }

    private void SaveSettingsInBackground(Action<AppSettings> update)
    {
        _ = Task.Run(() => _settingsService.Update(update));
    }

    private Task RunOnDispatcherAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            completion.TrySetCanceled();
        }

        return completion.Task;
    }

    private static string? GetModelIdentity(AppSettings settings)
    {
        var path = ModelPathResolver.FindExistingModelPath(settings) ?? settings.ModelPath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // ---- Recognition lifecycle ----

    public void ToggleMute()
    {
        if (!IsRecording) return;
        var newMuted = !IsCaptureMuted;
        _recognition.SetMuted(newMuted);
        IsCaptureMuted = newMuted;

        if (_stateMachine.IsActive)
            _stateMachine.Fire(newMuted ? RecognitionTrigger.Mute : RecognitionTrigger.Unmute);

        StatusText = newMuted ? "Muted (audio discarded)" : "Listening...";
        OnPropertyChanged(nameof(RecordingIndicator));
    }

    private async Task StartAsync()
    {
        if (IsRecording || IsModelLoading) return;
        ApplySettingsSnapshot(_settingsService.Load());

        // If model is not loaded, load it first
        if (_recognition.ModelState != ModelState.Loaded)
        {
            if (_recognition.ModelState == ModelState.Loading)
                return; // Already loading — no-op

            IsModelLoading = true;
            StatusText = "Loading model...";
            OnPropertyChanged(nameof(RecordButtonText));

            try
            {
                var modelPath = ModelPathResolver.FindExistingModelPath(_settings);
                if (modelPath is null)
                    throw new DirectoryNotFoundException("No downloaded model was found. Select or download a model in Settings.");

                _settings.ModelPath = modelPath;
                var settingsSnapshot = _settings.Clone();
                await Task.Run(() => _settingsService.Update(current => current.ModelPath = modelPath));

                await _recognition.LoadModelAsync(settingsSnapshot);
            }
            catch (Exception ex)
            {
                StatusText = $"Model load error: {ex.Message}";
                return;
            }
            finally
            {
                IsModelLoading = false;
                OnPropertyChanged(nameof(RecordButtonText));
            }

            if (_recognition.ModelState != ModelState.Loaded)
            {
                StatusText = "Model not ready";
                return;
            }
        }

        BeginTextTransition(_settings.ClearTextOnModelOrSessionChange);
        var foregroundWindow = _windowInterop.GetForegroundWindow();
        var ownWindow = _windowInterop.GetOwnWindowHandle();
        // Don't set injection target to our own window
        _injectionTargetWindow = (ownWindow != nint.Zero && foregroundWindow == ownWindow)
            ? nint.Zero
            : foregroundWindow;
        try
        {
            _stateMachine.Fire(RecognitionTrigger.Start);

            _currentSession = _sessionManager.CreateSession(
                _settings.Language, "Nemotron", _settings.AudioSource);

            _hook.Install();

            IsRecording = true;
            _partialResultTimer.Start();
            StatusText = "Listening...";

            await Task.Run(() =>
            {
                _recognition.Start(_settings);
            });
        }
        catch (Exception ex)
        {
            _stateMachine.Fire(RecognitionTrigger.Reset);

            Console.Error.WriteLine($"[VoiceType] Start error: {ex}");
            AppPaths.EnsureDataRoot();
            try { App.Telemetry?.LogError("Recognition", $"Start failed: {ex.Message}"); } catch { }
            _partialResultTimer.Stop();
            StatusText = $"Error: {ex.Message}";
            IsRecording = false;
        }
    }

    private void Stop()
    {
        if (!IsRecording) return;
        _hook.Uninstall();
        _recognition.Stop();
        StatusText = "Finalizing...";
    }

    // ---- Event handlers ----

    private void OnInputDetected()
    {
        if (!_settings.StopOnAnyInput) return;
        if (!IsRecording || IsCaptureMuted) return;

        // Pause audio processing instead of full stop: model stays loaded, recognition resumes quickly.
        _dispatcher.TryEnqueue(() =>
        {
            _recognition.SetMuted(true);
            IsCaptureMuted = true;
            StatusText = "Paused (model stays loaded)";
            OnPropertyChanged(nameof(RecordingIndicator));
        });
    }

    private bool CanInjectToTargetWindow()
    {
        var foregroundWindow = _windowInterop.GetForegroundWindow();
        if (_windowInterop.IsWindowInCurrentProcess(foregroundWindow))
            return false;

        if (_injectionExplicitlyEnabled) return true;
        if (!DisableInjectionOnFocusChange) return true;
        if (_injectionTargetWindow == nint.Zero) return true;
        return foregroundWindow == _injectionTargetWindow;
    }

    private void OnPartialResult(string text)
    {
        lock (_partialResultGate)
        {
            if (_hasPendingPartial && string.Equals(_pendingPartialText, text, StringComparison.Ordinal))
                return;

            _pendingPartialText = text;
            _hasPendingPartial = true;
        }
    }

    private void OnFinalResult(string text)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _partialResultTimer.Stop();
            FlushPendingPartialResult();
            _currentSessionText = text;
            UpdateDisplayedText();

            if (IsTextInjectionEnabled && _currentSessionText.Length > _lastInjectedLength && CanInjectToTargetWindow())
            {
                var delta = _currentSessionText[_lastInjectedLength..];
                var cleanedDelta = StripLeadingPunctuation(delta, _lastInjectedTextTail);
                if (!string.IsNullOrEmpty(cleanedDelta))
                    _textInjector.Inject(cleanedDelta, _settings.TextInjectionMethod);
                _lastInjectedTextTail = GetTextTail(_currentSessionText, 20);
            }
            _lastInjectedLength = 0;

            IsRecording = false;
            StatusText = "Ready";

            if (_currentSession is not null && _settings.SaveSessions)
            {
                _currentSession.EndedAt = DateTime.Now;
                _currentSession.RecognizedText = _currentSessionText;
                _currentSession.IsComplete = true;
                var sessionToSave = _currentSession;
                var saveAudio = _settings.SaveAudioMp3;
                _ = Task.Run(() => PersistSession(sessionToSave, saveAudio));
            }
        });
    }

    private void OnRecognitionStopped()
    {
        _dispatcher.TryEnqueue(() =>
        {
            _partialResultTimer.Stop();
            IsRecording = false;
            if (StatusText == "Finalizing...")
                StatusText = "Ready";
        });
    }

    private void FlushPendingPartialResult()
    {
        string? text;
        lock (_partialResultGate)
        {
            if (!_hasPendingPartial)
                return;

            text = _pendingPartialText;
            _hasPendingPartial = false;
        }

        if (string.IsNullOrEmpty(text))
            return;

        _currentSessionText = text;
        UpdateDisplayedText();

        if (!IsTextInjectionEnabled)
        {
            _lastInjectedLength = _currentSessionText.Length;
            return;
        }

        if (_currentSessionText.Length <= _lastInjectedLength)
            return;

        if (!CanInjectToTargetWindow())
        {
            _lastInjectedLength = _currentSessionText.Length;
            return;
        }

        var delta = _currentSessionText[_lastInjectedLength..];
        var cleanedDelta = StripLeadingPunctuation(delta, _lastInjectedTextTail);
        if (!string.IsNullOrEmpty(cleanedDelta))
            _textInjector.Inject(cleanedDelta, _settings.TextInjectionMethod);
        _lastInjectedTextTail = GetTextTail(_currentSessionText, 20);
        _lastInjectedLength = _currentSessionText.Length;
        _injectionExplicitlyEnabled = false;
    }

    /// <summary>
    /// Strips leading punctuation (. ! ? , ; :) from the delta when it appears
    /// at the start of a new injection chunk. This prevents the "dot at start"
    /// artifact where the ASR model emits sentence-final punctuation at the
    /// beginning of a streaming chunk.
    /// </summary>
    private static string StripLeadingPunctuation(string delta, string previousTail)
    {
        if (string.IsNullOrEmpty(delta))
            return delta;

        // Only strip if previous text ended with whitespace or sentence-final punctuation
        // (meaning a new sentence/word should start, not continue with punctuation)
        var shouldStrip = string.IsNullOrEmpty(previousTail)
            || previousTail.EndsWith(' ')
            || previousTail.EndsWith('.')
            || previousTail.EndsWith('!')
            || previousTail.EndsWith('?')
            || previousTail.EndsWith('\n');

        if (!shouldStrip)
            return delta;

        // Strip leading punctuation and whitespace: ". Hello" → "Hello"
        var i = 0;
        while (i < delta.Length && (char.IsPunctuation(delta[i]) || char.IsWhiteSpace(delta[i])))
            i++;

        return i > 0 ? delta[i..] : delta;
    }

    /// <summary>Returns the last N characters of text for tail comparison.</summary>
    private static string GetTextTail(string text, int maxLength)
        => text.Length <= maxLength ? text : text[^maxLength..];

    private void PersistSession(RecognitionSession session, bool saveAudio)
    {
        try
        {
            if (saveAudio)
                session.AudioFilePath = _recognition.SaveAudio(session.FileNameBase);

            _sessionManager.SaveSession(session);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceType] Session save error: {ex}");
        }
    }
}