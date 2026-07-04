using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceType.Models;
using VoiceType.Services;

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
    private readonly object _partialResultGate = new();
    private readonly DispatcherTimer _partialResultTimer;
    private string? _pendingPartialText;
    private bool _hasPendingPartial;
    private bool _isTextInjectionEnabled;
    private bool _isAutoScrollEnabled;
    private bool _disableInjectionOnFocusChange;

    public MainViewModel()
    {
        _settings = SettingsService.Load();

        _isTextInjectionEnabled = _settings.IsTextInjectionEnabled;
        _isAutoScrollEnabled = _settings.IsAutoScrollEnabled;
        _disableInjectionOnFocusChange = _settings.DisableInjectionOnFocusChange;

        StartCommand = new RelayCommand(Start, () => !IsRecording);
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
                _settings.IsTextInjectionEnabled = value;
                SettingsService.Save(_settings);
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

            OnPropertyChanged(nameof(RecordButtonText));
            OnPropertyChanged(nameof(RecordingIndicator));
        }
    }

    public string RecordButtonText => IsRecording ? "⏹ Stop" : "🎤 Start";
    public string RecordingIndicator => IsRecording
        ? (IsCaptureMuted ? "🔇 Muted" : "🔴 Recording...")
        : "⚪ Idle";

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenModelDownloaderCommand { get; }
    public ICommand MuteCommand { get; }

    // ── Hotkey ──────────────────────────────────────

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
            InjectCurrentText();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Manually inject the current recognized text into the focused window.
    /// Triggered by the InjectText hotkey.
    /// </summary>
    public void InjectCurrentText()
    {
        if (string.IsNullOrEmpty(_floatingText)) return;
        TextInjector.Inject(_floatingText, _settings.TextInjectionMethod);
    }

    // ── Commands ────────────────────────────────────

    public void Toggle()
    {
        if (IsRecording) Stop();
        else Start();
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
        var window = new Views.ModelDownloaderWindow();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
        if (window.WasDownloaded && window.ResultModelPath is not null)
        {
            _settings.ModelsRootPath = window.ResultPath ?? _settings.ModelsRootPath;
            _settings.ModelPath = window.ResultModelPath;
            SettingsService.Save(_settings);
        }
    }

    private void Start()
    {
        if (IsRecording) return;
        ApplySettingsSnapshot(SettingsService.Load());

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
            _recognition.Initialize(_settings);
            Console.WriteLine("[VoiceType] Recognizer initialized OK");

            _currentSession = SessionManager.CreateSession(
                _settings.Language, "Nemotron", _settings.AudioSource);

            Console.WriteLine("[VoiceType] Installing global hooks...");
            _hook.Install();
            Console.WriteLine("[VoiceType] Hooks installed OK");

            IsRecording = true;
            _partialResultTimer.Start();
            StatusText = "Listening...";

            Console.WriteLine("[VoiceType] Starting recognition...");
            _recognition.Start(_settings);
            Console.WriteLine("[VoiceType] Recognition started OK");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceType] Start error: {ex}");
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

    private void OpenSettings()
    {
        var settingsWindow = new Views.SettingsWindow(_settings);
        settingsWindow.Owner = Application.Current.MainWindow;
        if (settingsWindow.ShowDialog() == true)
        {
            ApplySettingsSnapshot(settingsWindow.ResultSettings);
            // Re-register hotkey with new binding
            var hwnd = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle;
            RegisterHotkey(hwnd);
        }
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
        Application.Current.Dispatcher.BeginInvoke(() =>
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

        if (!IsTextInjectionEnabled || text.Length <= _lastInjectedLength)
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
    }

    private void PersistSession(RecognitionSession session, bool saveAudio)
    {
        try
        {
            if (saveAudio)
                session.AudioFilePath = _recognition.SaveAudio(session.Id);

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
