using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Shell;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceType.Models;
using VoiceType.Services;
using VoiceType.Services.Intents;

namespace VoiceType.ViewModels;

/// <summary>
/// ViewModel for the main floating recognition window.
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
    private readonly DispatcherTimer _partialResultTimer;
    private string? _pendingPartialText;
    private bool _hasPendingPartial;
    private bool _isTextInjectionEnabled;
    private bool _isAutoScrollEnabled;
    private bool _disableInjectionOnFocusChange;
    private bool _isActivelyInjecting;
    private bool _injectionExplicitlyEnabled;
    private bool _isInitializing;

    // ── Voice Intent Mode (optional) ────────────────
    private VoiceIntentRouter? _intentRouter;
    private InjectionController? _injectionController;
    private WindowTargetHistory? _windowTargetHistory;
    private Views.ClarifyTargetDialog? _clarifyDialog;
    private bool _voiceIntentActive;

    /// <summary>True while the optional voice-command mode is live (router constructed).</summary>
    public bool IsVoiceIntentActive
    {
        get => _voiceIntentActive;
        private set => SetProperty(ref _voiceIntentActive, value);
    }

    public MainViewModel()
    {
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

        _partialResultTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background,
            (_, _) => FlushPendingPartialResult(),
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
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

    public string RecordButtonText => IsInitializing ? "⏳ Initializing..." : (IsRecording ? "⏹ Stop" : "🎤 Start");
    public string RecordingIndicator => IsRecording
        ? (IsCaptureMuted ? "🔇 Muted" : "🔴 Recording...")
        : "⚪ Idle";

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
    /// True when the app is recording AND text injection is enabled —
    /// drives the taskbar progress indicator.
    /// </summary>
    public bool IsActivelyInjecting
    {
        get => _isActivelyInjecting;
        private set
        {
            if (SetProperty(ref _isActivelyInjecting, value))
                OnPropertyChanged(nameof(TaskbarProgressState));
        }
    }

    /// <summary>
    /// Shows a green pulsing indicator on the taskbar button when
    /// the app is actively injecting recognized text into a target window.
    /// </summary>
    public TaskbarItemProgressState TaskbarProgressState =>
        IsActivelyInjecting
            ? TaskbarItemProgressState.Indeterminate
            : TaskbarItemProgressState.None;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenModelDownloaderCommand { get; }
    public ICommand MuteCommand { get; }

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
            System.Windows.Clipboard.SetText(_floatingText);
    }

    private void OpenModelDownloader()
    {
        var existingWindow = Views.ModelDownloaderWindow.FindOpenInstance();
        if (existingWindow is not null)
        {
            existingWindow.Activate();
            return;
        }

        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current.MainWindow;
        var window = new Views.ModelDownloaderWindow();
        window.Owner = owner;
        window.Closed += (_, _) =>
        {
            if (window.WasDownloaded && window.ResultModelPath is not null)
            {
                _settings.ModelsRootPath = window.ResultPath ?? _settings.ModelsRootPath;
                _settings.ModelPath = window.ResultModelPath;
                SettingsService.Save(_settings);
            }
        };
        window.Show();
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
        if (_settingsWindow?.IsLoaded == true)
        {
            _settingsWindow.Activate();
            return;
        }

        var settingsWindow = new Views.SettingsWindow(_settings);
        settingsWindow.Owner = Application.Current.MainWindow;
        settingsWindow.Closed += (_, _) =>
        {
            if (settingsWindow.WasSaved)
            {
                ApplySettingsSnapshot(settingsWindow.ResultSettings);
                // Re-register hotkey with new binding
                var hwnd = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle;
                RegisterHotkey(hwnd);
            }
            _settingsWindow = null;
        };
        _settingsWindow = settingsWindow;
        settingsWindow.Show();
    }

    // ── Event handlers ──────────────────────────────

    private void OnInputDetected()
    {
        if (_settings.StopOnAnyInput)
        {
            Application.Current.Dispatcher.BeginInvoke(Stop);
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

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _partialResultTimer.Stop();
            FlushPendingPartialResult();
            RecognizedText = text;
            FloatingText = text;

            // Voice Intent Mode: a final utterance may be a command (handled) or
            // an answer to an open clarify dialog. When handled, skip injection.
            if (!TryHandleVoiceCommand(text))
            {
                // Inject any remaining delta not yet sent
                if (IsTextInjectionEnabled && text.Length > _lastInjectedLength && CanInjectToTargetWindow())
                {
                    var delta = text[_lastInjectedLength..];
                    InjectDelta(delta);
                }
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
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _partialResultTimer.Stop();
            IsRecording = false;
            _intentRouter?.Reset();
            _clarifyDialog?.Close();
            _clarifyDialog = null;
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
        InjectDelta(delta);
        _lastInjectedLength = text.Length;
        _injectionExplicitlyEnabled = false;
    }

    /// <summary>
    /// Injects a text delta. In Voice Intent Mode this routes through
    /// <see cref="InjectionController"/> so the target window/field is recorded;
    /// otherwise it calls <see cref="TextInjector"/> directly (unchanged path).
    /// </summary>
    private void InjectDelta(string delta)
    {
        if (_injectionController is not null)
            _injectionController.Inject(delta, _settings.TextInjectionMethod, _settings.VoiceIntents.RememberTargets);
        else
            TextInjector.Inject(delta, _settings.TextInjectionMethod);
    }

    // ── Voice Intent Mode ───────────────────────────

    /// <summary>
    /// Constructs the intent router + injection controller when the mode is
    /// enabled. When disabled, both stay null and the dictation pipeline is
    /// bit-for-bit the legacy path (no model loaded, no extra work).
    /// </summary>
    private void EnsureVoiceIntentServices()
    {
        if (!_settings.VoiceIntents.Enabled)
        {
            TeardownVoiceIntentServices();
            return;
        }

        if (_intentRouter is not null)
            return; // already live

        try
        {
            var historyPath = System.IO.Path.Combine(AppPaths.DataRoot, "window-targets.json");
            _windowTargetHistory = new WindowTargetHistory(historyPath, _settings.VoiceIntents.HistoryPerProcess);

            IIntentScorer? scorer = null;
            var modelPath = _settings.VoiceIntents.IntentModelPath;
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                try
                {
                    var taxonomy = VoiceIntentTaxonomy.Load(_settings.VoiceIntents.CustomIntentsFile);
                    scorer = new MiniLmIntentScorer(modelPath, taxonomy);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[VoiceType] Intent model failed to load, L0-only: {ex.Message}");
                    scorer = null;
                }
            }

            _intentRouter = new VoiceIntentRouter(_settings.VoiceIntents, l1: scorer);
            _injectionController = new InjectionController(_windowTargetHistory);
            IsVoiceIntentActive = true;
            Console.WriteLine($"[VoiceType] Voice Intent Mode active (semantic={(scorer is not null)})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceType] Voice Intent Mode init failed: {ex}");
            TeardownVoiceIntentServices();
        }
    }

    private void TeardownVoiceIntentServices()
    {
        _intentRouter?.Dispose();
        _intentRouter = null;
        _injectionController = null;
        _windowTargetHistory = null;
        IsVoiceIntentActive = false;
    }

    /// <summary>
    /// Routes a final utterance through the intent cascade. Returns true when the
    /// utterance was consumed as a command or clarify answer (so it is not typed).
    /// </summary>
    private bool TryHandleVoiceCommand(string text)
    {
        if (_intentRouter is null || string.IsNullOrWhiteSpace(text))
            return false;

        // An open clarify dialog gets first claim on the utterance.
        if (_clarifyDialog is not null)
        {
            if (_clarifyDialog.TryHandleVoice(text))
            {
                var chosen = _clarifyDialog.SelectedTarget;
                _clarifyDialog = null;
                if (chosen is not null)
                    ApplyRetarget(chosen);
                return true;
            }
            // Not a selection — let it fall through as normal dictation.
            return false;
        }

        VoiceIntentResult result;
        try
        {
            result = _intentRouter.RouteAsync(text).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceType] Intent route error: {ex.Message}");
            return false;
        }

        if (!result.IsCommand)
            return false;

        switch (result.Intent)
        {
            case VoiceIntentIds.StartTyping:
                IsTextInjectionEnabled = true;
                StatusText = "🎙 Voice: typing enabled";
                return true;

            case VoiceIntentIds.StopTyping:
                IsTextInjectionEnabled = false;
                StatusText = "🎙 Voice: typing stopped";
                return true;

            case VoiceIntentIds.SwitchTarget:
                return HandleSwitchTarget(text);

            case VoiceIntentIds.Cancel:
                if (_clarifyDialog is not null)
                {
                    _clarifyDialog.Close();
                    _clarifyDialog = null;
                }
                StatusText = "🎙 Voice: cancelled";
                return true;

            default:
                return false;
        }
    }

    /// <summary>Resolve "type into &lt;app&gt;" to a remembered target, clarifying when ambiguous.</summary>
    private bool HandleSwitchTarget(string spokenText)
    {
        if (_windowTargetHistory is null)
            return false;

        var appName = ExtractSpokenAppName(spokenText);
        var candidates = _windowTargetHistory.FindCandidates(appName, _settings.VoiceIntents.AppAliases);

        if (candidates.Count == 0)
        {
            StatusText = $"🎙 No remembered target for \"{appName}\"";
            return true; // consumed the command; nothing to switch to
        }

        if (candidates.Count == 1 || !_settings.VoiceIntents.ConfirmTargetSwitch)
        {
            ApplyRetarget(candidates[0]);
            return true;
        }

        // Ambiguous → open the clarify dialog (voice or click to choose).
        var dialog = new Views.ClarifyTargetDialog(appName, candidates)
        {
            Owner = Application.Current.MainWindow
        };
        _clarifyDialog = dialog;
        dialog.Closed += (_, _) => { if (ReferenceEquals(_clarifyDialog, dialog)) _clarifyDialog = null; };
        dialog.Show();
        StatusText = $"🎙 Which field of \"{appName}\"?";
        return true;
    }

    /// <summary>Focus a remembered window (and field) and make it the injection target.</summary>
    private void ApplyRetarget(WindowTarget target)
    {
        var hwnd = InjectionController.FindWindowByProcess(target.ProcessName, target.WindowTitle);
        if (hwnd == nint.Zero || !InjectionController.TryFocusWindow(hwnd, target))
        {
            StatusText = $"🎙 Switch to \"{target.ProcessName}\" manually";
            return;
        }

        _injectionTargetWindow = hwnd;
        IsTextInjectionEnabled = true;
        StatusText = $"🎙 Typing into {target.DisplayLabel}";
    }

    /// <summary>Extract the app name after a switch-target trigger phrase.</summary>
    private static string ExtractSpokenAppName(string text)
    {
        var lower = text.ToLowerInvariant();
        string[] markers =
        {
            "набери в", "напечатай в", "переключись в", "печатай в", "введи в",
            "type this in", "type into", "switch to", "write in", "print in",
        };
        foreach (var marker in markers)
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = text[(idx + marker.Length)..].Trim().TrimEnd('.', '?', '!');
                if (rest.Length > 0)
                    return rest;
            }
        }
        return text.Trim();
    }

    /// <summary>Open the separate Voice Commands settings window.</summary>
    public void OpenVoiceCommandsSettings()
    {
        var window = new Views.VoiceCommandsSettingsWindow(_settings.VoiceIntents, _windowTargetHistory)
        {
            Owner = Application.Current.MainWindow
        };
        window.Closed += (_, _) =>
        {
            if (window.WasSaved)
            {
                SettingsService.Save(_settings);
                ApplySettingsSnapshot(_settings); // re-evaluates Voice Intent services
            }
        };
        window.Show();
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

        // Re-evaluate the optional Voice Intent Mode against the new settings.
        EnsureVoiceIntentServices();

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
