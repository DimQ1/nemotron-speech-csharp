using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

/// <summary>
/// ViewModel for the main floating recognition window.
/// WinUI 3: DispatcherQueue instead of Dispatcher, no CommandManager.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private readonly RecognitionService _recognition = new();
    private readonly GlobalInputHook _hook = new();
    private AppSettings _settings;
    private string _statusText = "Ready";
    private string _recognizedText = "";
    private string _floatingText = "";
    private int _lastInjectedLength;
    private bool _isRecording;
    private bool _isCaptureMuted;
    private int _toggleHotkeyId;
    private int _muteHotkeyId;
    private int _injectTextHotkeyId;
    private nint _injectionTargetWindow;
    private RecognitionSession? _currentSession;
    private Views.SettingsWindow? _settingsWindow;
    private readonly object _partialResultGate = new();
    private readonly DispatcherQueueTimer _partialResultTimer;
    private string? _pendingPartialText;
    private bool _hasPendingPartial;
    private bool _isTextInjectionEnabled;
    private bool _isAutoScrollEnabled;
    private bool _disableInjectionOnFocusChange;
    private bool _isActivelyInjecting;
    private bool _injectionExplicitlyEnabled;
    private bool _isInitializing;
    private bool _isModelAvailable;
    private string _modelStatusText = "";
    private readonly DispatcherQueue _dispatcher;

    /// <summary>HWND of the main window — set by MainWindow after loading.</summary>
    public nint MainWindowHandle { get; set; }

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = SettingsService.Load();

        _isTextInjectionEnabled = _settings.IsTextInjectionEnabled;
        _isAutoScrollEnabled = _settings.IsAutoScrollEnabled;
        _disableInjectionOnFocusChange = _settings.DisableInjectionOnFocusChange;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRecording && !IsInitializing);
        StopCommand = new RelayCommand(Stop, () => IsRecording);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ToggleCommand = new RelayCommand(Toggle);
        CopyCommand = new RelayCommand(CopyText);
        OpenModelDownloaderCommand = new RelayCommand(OpenModelDownloader);
        MuteCommand = new RelayCommand(ToggleMute, () => IsRecording);

        _hook.InputDetected += OnInputDetected;
        _recognition.PartialResult += OnPartialResult;
        _recognition.FinalResult += OnFinalResult;
        _recognition.Stopped += OnRecognitionStopped;

        _partialResultTimer = _dispatcher.CreateTimer();
        _partialResultTimer.Interval = TimeSpan.FromMilliseconds(50);
        _partialResultTimer.Tick += (_, _) => FlushPendingPartialResult();

        // Check model availability on startup
        CheckModelAvailability();
    }

    // ── Properties ──────────────────────────────────

    /// <summary>Current settings snapshot (for persistence on exit).</summary>
    internal AppSettings Settings => _settings;

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string RecognizedText { get => _recognizedText; set => SetProperty(ref _recognizedText, value); }
    public string FloatingText { get => _floatingText; set => SetProperty(ref _floatingText, value); }
    public bool IsTextInjectionEnabled
    {
        get => _isTextInjectionEnabled;
        set
        {
            if (SetProperty(ref _isTextInjectionEnabled, value))
            {
                _lastInjectedLength = _floatingText.Length;
                _settings.IsTextInjectionEnabled = value;
                SettingsService.Save(_settings);

                if (value)
                {
                    _injectionTargetWindow = GetForegroundWindow();
                    _injectionExplicitlyEnabled = true;

                    if (!IsRecording && !IsInitializing)
                        _ = StartAsync();
                }

                IsActivelyInjecting = _isTextInjectionEnabled && IsRecording;
            }
        }
    }

    public bool IsAutoScrollEnabled
    {
        get => _isAutoScrollEnabled;
        set
        {
            if (SetProperty(ref _isAutoScrollEnabled, value))
            {
                _settings.IsAutoScrollEnabled = value;
                SettingsService.Save(_settings);
            }
        }
    }

    public bool DisableInjectionOnFocusChange
    {
        get => _disableInjectionOnFocusChange;
        set
        {
            if (SetProperty(ref _disableInjectionOnFocusChange, value))
            {
                _settings.DisableInjectionOnFocusChange = value;
                SettingsService.Save(_settings);
            }
        }
    }
    public bool IsCaptureMuted
    {
        get => _isCaptureMuted;
        set
        {
            if (SetProperty(ref _isCaptureMuted, value))
                OnPropertyChanged(nameof(RecordingIndicator));
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (!SetProperty(ref _isRecording, value))
                return;

            IsActivelyInjecting = value && IsTextInjectionEnabled;
            OnPropertyChanged(nameof(RecordButtonText));
            OnPropertyChanged(nameof(RecordingIndicator));
        }
    }

    public string RecordButtonText => IsInitializing ? "Initializing..." : (IsRecording ? "Stop" : "Start");
    public string RecordingIndicator => IsRecording
        ? (IsCaptureMuted ? "Muted" : "Recording...")
        : "Idle";

    /// <summary>True while the recognition engine is being initialized (blocks Start command).</summary>
    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (SetProperty(ref _isInitializing, value))
                OnPropertyChanged(nameof(RecordButtonText));
        }
    }

    /// <summary>
    /// True when the app is recording AND text injection is enabled.
    /// </summary>
    public bool IsActivelyInjecting
    {
        get => _isActivelyInjecting;
        private set => SetProperty(ref _isActivelyInjecting, value);
    }

    public System.Windows.Input.ICommand StartCommand { get; }
    public System.Windows.Input.ICommand StopCommand { get; }
    public System.Windows.Input.ICommand OpenSettingsCommand { get; }
    public System.Windows.Input.ICommand ToggleCommand { get; }
    public System.Windows.Input.ICommand CopyCommand { get; }
    public System.Windows.Input.ICommand OpenModelDownloaderCommand { get; }
    public System.Windows.Input.ICommand MuteCommand { get; }

    // ── Model availability ──────────────────────────

    /// <summary>True when a usable model is found on disk.</summary>
    public bool IsModelAvailable
    {
        get => _isModelAvailable;
        private set
        {
            if (SetProperty(ref _isModelAvailable, value))
                OnPropertyChanged(nameof(ShowModelWarning));
        }
    }

    /// <summary>Status text about model availability (shown in UI).</summary>
    public string ModelStatusText
    {
        get => _modelStatusText;
        private set => SetProperty(ref _modelStatusText, value);
    }

    /// <summary>Show warning banner when no model is available.</summary>
    public bool ShowModelWarning => !IsModelAvailable;

    /// <summary>Recommended model repo for quick download.</summary>
    public static string RecommendedModelRepo => "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu";
    public static string RecommendedModelDisplay => "CPU (INT4, opset24, 0.56s) — fast, low latency, ~749 MB";

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

    // ── Hotkey ──────────────────────────────────────

    /// <summary>
    /// Called after the window is fully loaded. Starts recognition automatically
    /// if <see cref="AppSettings.AutoStartRecognition"/> is enabled.
    /// </summary>
    public void TryAutoStart()
    {
        if (_settings.AutoStartRecognition && !IsRecording && !IsInitializing)
            _ = StartAsync();
    }

    public void RegisterHotkey(nint hwnd)
    {
        GlobalHotkeyService.UnregisterAll();
        _toggleHotkeyId = 0;
        _muteHotkeyId = 0;
        _injectTextHotkeyId = 0;

        var toggle = _settings.ToggleHotkey;
        if (!string.IsNullOrEmpty(toggle))
            _toggleHotkeyId = GlobalHotkeyService.Register(hwnd, toggle);

        var mute = _settings.MuteHotkey;
        if (!string.IsNullOrEmpty(mute))
            _muteHotkeyId = GlobalHotkeyService.Register(hwnd, mute);

        var inject = _settings.InjectTextHotkey;
        if (!string.IsNullOrEmpty(inject))
            _injectTextHotkeyId = GlobalHotkeyService.Register(hwnd, inject);
    }

    /// <summary>Handle WM_HOTKEY by hotkey ID. Returns true if handled.</summary>
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

    /// <summary>Toggle automatic text injection from the global hotkey.</summary>
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

    /// <summary>Manually inject the current recognized text into the focused window.</summary>
    public void InjectCurrentText()
    {
        if (!IsTextInjectionEnabled) return;
        if (string.IsNullOrEmpty(_floatingText)) return;
        TextInjector.Inject(_floatingText, _settings.TextInjectionMethod);
    }

    // ── Commands ────────────────────────────────────

    public void Toggle()
    {
        if (IsRecording) Stop();
        else if (!IsInitializing) _ = StartAsync();
    }

    public void ToggleMute()
    {
        if (!IsRecording) return;
        var newMuted = !IsCaptureMuted;
        _recognition.SetMuted(newMuted);
        IsCaptureMuted = newMuted;
        StatusText = newMuted ? "Muted (audio discarded)" : "Listening...";
        OnPropertyChanged(nameof(RecordingIndicator));
    }

    private void CopyText()
    {
        if (!string.IsNullOrEmpty(_floatingText))
            TextInjector.CopyToClipboard(_floatingText);
    }

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
                _settings.ModelsRootPath = window.ViewModel.ResultPath ?? _settings.ModelsRootPath;
                _settings.ModelPath = window.ViewModel.ResultModelPath;
                SettingsService.Save(_settings);
            }
        };
        window.Activate();
    }

    private async Task StartAsync()
    {
        if (IsRecording || IsInitializing) return;
        ApplySettingsSnapshot(SettingsService.Load());

        IsInitializing = true;
        StatusText = "Initializing engine...";
        RecognizedText = "";
        FloatingText = "";
        _lastInjectedLength = 0;
        _injectionTargetWindow = GetForegroundWindow();
        lock (_partialResultGate)
        {
            _pendingPartialText = null;
            _hasPendingPartial = false;
        }

        try
        {
            // Compute ModelPath from root + selection if not set directly
            if (string.IsNullOrEmpty(_settings.ModelPath) && !string.IsNullOrEmpty(_settings.ModelsRootPath))
            {
                _settings.ModelPath = Path.Combine(_settings.ModelsRootPath, _settings.SelectedModel);
                SettingsService.Save(_settings);
            }

            Console.WriteLine($"[VoiceType] Initializing recognizer: path={_settings.ModelPath}, ep={_settings.ExecutionProvider}, lang={_settings.Language}, vad={_settings.UseVad}");

            // Run heavy initialization on background thread to keep UI responsive
            await Task.Run(() =>
            {
                _recognition.Initialize(_settings);
                Console.WriteLine("[VoiceType] Recognizer initialized OK");
            });

            _currentSession = SessionManager.CreateSession(
                _settings.Language, "Nemotron", _settings.AudioSource);

            Console.WriteLine("[VoiceType] Installing global hooks...");
            _hook.Install();
            Console.WriteLine("[VoiceType] Hooks installed OK");

            IsRecording = true;
            _partialResultTimer.Start();
            StatusText = "Listening...";

            Console.WriteLine("[VoiceType] Starting recognition...");

            // Start recognition on background thread (includes warmup)
            await Task.Run(() =>
            {
                _recognition.Start(_settings);
                Console.WriteLine("[VoiceType] Recognition started OK");
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceType] Start error: {ex}");
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
                ApplySettingsSnapshot(settingsWindow.ViewModel.BuildSettings());
                // Re-register hotkey with new binding
                if (MainWindowHandle != nint.Zero)
                    RegisterHotkey(MainWindowHandle);
            }
            _settingsWindow = null;
        };
        _settingsWindow = settingsWindow;
        settingsWindow.Activate();
    }

    // ── Event handlers ──────────────────────────────

    private void OnInputDetected()
    {
        if (_settings.StopOnAnyInput)
        {
            _dispatcher.TryEnqueue(Stop);
        }
    }

    /// <summary>
    /// Returns true if text injection is allowed into the current foreground window.
    /// When <see cref="DisableInjectionOnFocusChange"/> is enabled, this checks that
    /// the foreground window hasn't changed since recording started.
    /// </summary>
    private bool CanInjectToTargetWindow()
    {
        if (_injectionExplicitlyEnabled) return true;
        if (!_disableInjectionOnFocusChange) return true;
        if (_injectionTargetWindow == nint.Zero) return true;
        return GetForegroundWindow() == _injectionTargetWindow;
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

            // Inject any remaining delta not yet sent
            if (IsTextInjectionEnabled && text.Length > _lastInjectedLength && CanInjectToTargetWindow())
            {
                var delta = text[_lastInjectedLength..];
                TextInjector.Inject(delta, _settings.TextInjectionMethod);
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
            // Focus changed — skip injection but keep tracking length
            _lastInjectedLength = text.Length;
            return;
        }

        var delta = text[_lastInjectedLength..];
        TextInjector.Inject(delta, _settings.TextInjectionMethod);
        _lastInjectedLength = text.Length;
        _injectionExplicitlyEnabled = false;
    }

    private void PersistSession(RecognitionSession session, bool saveAudio)
    {
        try
        {
            if (saveAudio)
                session.AudioFilePath = _recognition.SaveAudio(session.FileNameBase);

            SessionManager.SaveSession(session);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceType] Session save error: {ex}");
        }
    }

    private void ApplySettingsSnapshot(AppSettings settings)
    {
        _settings = settings;

        _isTextInjectionEnabled = settings.IsTextInjectionEnabled;
        _isAutoScrollEnabled = settings.IsAutoScrollEnabled;
        _disableInjectionOnFocusChange = settings.DisableInjectionOnFocusChange;

        OnPropertyChanged(nameof(IsTextInjectionEnabled));
        OnPropertyChanged(nameof(IsAutoScrollEnabled));
        OnPropertyChanged(nameof(DisableInjectionOnFocusChange));
    }

    // ── INotifyPropertyChanged ──────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
