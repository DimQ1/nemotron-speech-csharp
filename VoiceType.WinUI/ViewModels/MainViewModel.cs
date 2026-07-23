using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.Services.Recognition;

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

    // ---- Observable properties (source-generated) ----

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _recognizedText = "";

    [ObservableProperty]
    private string _floatingText = "";

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
    private bool _isInitializing;

    [ObservableProperty]
    private bool _isModelAvailable;

    [ObservableProperty]
    private string _modelStatusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelWarning))]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _isActivelyInjecting;

    public nint MainWindowHandle { get; set; }

    // ---- Computed properties ----

    public string RecordButtonText => IsInitializing ? "Initializing..." : (IsRecording ? "Stop" : "Start");

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

        IsTextInjectionEnabled = _settings.IsTextInjectionEnabled;
        IsAutoScrollEnabled = _settings.IsAutoScrollEnabled;
        DisableInjectionOnFocusChange = _settings.DisableInjectionOnFocusChange;
        AlwaysOnTop = _settings.AlwaysOnTop;

        _hook.InputDetected += OnInputDetected;
        _recognition.PartialResult += OnPartialResult;
        _recognition.FinalResult += OnFinalResult;
        _recognition.Stopped += OnRecognitionStopped;

        _partialResultTimer = _dispatcher.CreateTimer();
        _partialResultTimer.Interval = TimeSpan.FromMilliseconds(50);
        _partialResultTimer.Tick += (_, _) => FlushPendingPartialResult();

        // Listen for ModelDownloaded messages
        WeakReferenceMessenger.Default.Register<ModelDownloadedMessage>(this, (r, m) =>
        {
            _settings.ModelsRootPath = m.Value.ModelsRootPath;
            _settings.ModelPath = m.Value.ModelPath;
            _settingsService.Save(_settings);
        });

        CheckModelAvailability();
    }

    // ---- Property change hooks ----

    partial void OnIsTextInjectionEnabledChanged(bool value)
    {
        _lastInjectedLength = FloatingText.Length;
        _settings.IsTextInjectionEnabled = value;
        _settingsService.Save(_settings);

        if (value)
        {
            _injectionTargetWindow = _windowInterop.GetForegroundWindow();
            _injectionExplicitlyEnabled = true;

            if (!IsRecording && !IsInitializing)
                _ = StartAsync();
        }

        IsActivelyInjecting = IsTextInjectionEnabled && IsRecording;
    }

    partial void OnIsAutoScrollEnabledChanged(bool value)
    {
        _settings.IsAutoScrollEnabled = value;
        _settingsService.Save(_settings);
    }

    partial void OnDisableInjectionOnFocusChangeChanged(bool value)
    {
        _settings.DisableInjectionOnFocusChange = value;
        _settingsService.Save(_settings);
    }

    partial void OnAlwaysOnTopChanged(bool value)
    {
        _settings.AlwaysOnTop = value;
        _settingsService.Save(_settings);
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

    partial void OnIsInitializingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordButtonText));
    }

    partial void OnIsModelAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowModelWarning));
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
        else if (!IsInitializing) _ = StartAsync();
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
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var settingsWindow = new Views.SettingsWindow(_settings);
        settingsWindow.Closed += (_, _) =>
        {
            if (settingsWindow.ViewModel.WasSaved)
            {
                var newSettings = settingsWindow.ViewModel.BuildSettings();
                ApplySettingsSnapshot(newSettings);

                if (MainWindowHandle != nint.Zero)
                    RegisterHotkey(MainWindowHandle);

                WeakReferenceMessenger.Default.Send(new SettingsSavedMessage(newSettings));
            }
            _settingsWindow = null;
        };
        _settingsWindow = settingsWindow;
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

        var window = new Views.ModelDownloaderWindow();
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

    // ---- Hotkey ----

    public void TryAutoStart()
    {
        if (_settings.AutoStartRecognition && !IsRecording && !IsInitializing)
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
        _textInjector.Inject(_floatingText, _settings.TextInjectionMethod);
    }

    // ---- Model availability ----

    public void DismissModelWarning()
    {
        _modelWarningDismissed = true;
        OnPropertyChanged(nameof(ShowModelWarning));
    }

    private void CheckModelAvailability()
    {
        var modelPath = _settings.ModelPath;
        if (string.IsNullOrEmpty(modelPath) && !string.IsNullOrEmpty(_settings.ModelsRootPath))
            modelPath = Path.Combine(_settings.ModelsRootPath, _settings.SelectedModel);

        if (!string.IsNullOrEmpty(modelPath) && Directory.Exists(modelPath))
        {
            var configPath = Path.Combine(modelPath, "genai_config.json");
            IsModelAvailable = File.Exists(configPath);
            ModelStatusText = IsModelAvailable
                ? $"Model ready: {Path.GetFileName(modelPath)}"
                : $"Model folder exists but genai_config.json missing: {modelPath}";
        }
        else
        {
            IsModelAvailable = false;
            ModelStatusText = "No model found. Download recommended:";
        }
    }

    private void ApplySettingsSnapshot(AppSettings settings)
    {
        _settings = settings;
        _isTextInjectionEnabled = settings.IsTextInjectionEnabled;
        _isAutoScrollEnabled = settings.IsAutoScrollEnabled;
        _disableInjectionOnFocusChange = settings.DisableInjectionOnFocusChange;
        _alwaysOnTop = settings.AlwaysOnTop;

        OnPropertyChanged(nameof(IsTextInjectionEnabled));
        OnPropertyChanged(nameof(IsAutoScrollEnabled));
        OnPropertyChanged(nameof(DisableInjectionOnFocusChange));
        OnPropertyChanged(nameof(AlwaysOnTop));
    }

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
        if (IsRecording || IsInitializing) return;
        ApplySettingsSnapshot(_settingsService.Load());

        IsInitializing = true;
        StatusText = "Initializing engine...";
        RecognizedText = "";
        FloatingText = "";
        _lastInjectedLength = 0;
        _injectionTargetWindow = _windowInterop.GetForegroundWindow();
        lock (_partialResultGate)
        {
            _pendingPartialText = null;
            _hasPendingPartial = false;
        }

        try
        {
            _stateMachine.Fire(RecognitionTrigger.Start);

            if (string.IsNullOrEmpty(_settings.ModelPath) && !string.IsNullOrEmpty(_settings.ModelsRootPath))
            {
                _settings.ModelPath = Path.Combine(_settings.ModelsRootPath, _settings.SelectedModel);
                _settingsService.Save(_settings);
            }

            await Task.Run(() =>
            {
                _recognition.Initialize(_settings);
            });

            _stateMachine.Fire(RecognitionTrigger.InitOk);

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
            if (_stateMachine.CurrentState == RecognitionState.Initializing)
                _stateMachine.Fire(RecognitionTrigger.InitFail);

            Console.Error.WriteLine($"[VoiceType] Start error: {ex}");
            AppPaths.EnsureDataRoot();
            File.AppendAllText(AppPaths.ErrorLogFile, $"[{DateTime.Now}] Recognition initialization failed: {ex}\n");
            _partialResultTimer.Stop();
            StatusText = $"Error: {ex.Message}";
            IsRecording = false;
        }
        finally
        {
            IsInitializing = false;
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
            StatusText = "Paused (model loaded)";
            OnPropertyChanged(nameof(RecordingIndicator));
        });
    }

    private bool CanInjectToTargetWindow()
    {
        if (_injectionExplicitlyEnabled) return true;
        if (!DisableInjectionOnFocusChange) return true;
        if (_injectionTargetWindow == nint.Zero) return true;
        return _windowInterop.GetForegroundWindow() == _injectionTargetWindow;
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

    private async void OnFinalResult(string text)
    {
        RecognitionSession? sessionToSave = null;
        bool saveAudio = false;

        _dispatcher.TryEnqueue(() =>
        {
            _partialResultTimer.Stop();
            FlushPendingPartialResult();
            RecognizedText = text;
            FloatingText = text;

            if (IsTextInjectionEnabled && text.Length > _lastInjectedLength && CanInjectToTargetWindow())
            {
                var delta = text[_lastInjectedLength..];
                _textInjector.Inject(delta, _settings.TextInjectionMethod);
            }
            _lastInjectedLength = 0;

            IsRecording = false;
            StatusText = "Done";

            if (_currentSession is not null && _settings.SaveSessions)
            {
                _currentSession.EndedAt = DateTime.Now;
                _currentSession.RecognizedText = text;
                _currentSession.IsComplete = true;
                sessionToSave = _currentSession;
                saveAudio = _settings.SaveAudioMp3;
            }
        });

        if (sessionToSave is null)
            return;

        await Task.Run(() => PersistSession(sessionToSave, saveAudio));
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

        RecognizedText = text;
        FloatingText = text;

        if (!IsTextInjectionEnabled)
        {
            _lastInjectedLength = text.Length;
            return;
        }

        if (text.Length <= _lastInjectedLength)
            return;

        if (!CanInjectToTargetWindow())
        {
            _lastInjectedLength = text.Length;
            return;
        }

        var delta = text[_lastInjectedLength..];
        _textInjector.Inject(delta, _settings.TextInjectionMethod);
        _lastInjectedLength = text.Length;
        _injectionExplicitlyEnabled = false;
    }

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