using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly RecognitionService _recognition = new();
    private readonly GlobalInputHook _hook = new();
    private AppSettings _settings;
    private string _statusText = "Ready";
    private string _recognizedText = "";
    private string _floatingText = "";
    private int _lastInjectedLength;
    private bool _isRecording;
    private RecognitionSession? _currentSession;
    private readonly object _partialResultGate = new();
    private readonly DispatcherTimer _partialResultTimer;
    private string? _pendingPartialText;
    private bool _hasPendingPartial;
    private bool _isTextInjectionEnabled = true;

    public MainViewModel()
    {
        _settings = SettingsService.Load();
        StartCommand = new RelayCommand(Start, () => !IsRecording);
        StopCommand = new RelayCommand(Stop, () => IsRecording);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ToggleCommand = new RelayCommand(Toggle);
        CopyCommand = new RelayCommand(CopyText);
        OpenModelDownloaderCommand = new RelayCommand(OpenModelDownloader);

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

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string RecognizedText { get => _recognizedText; set => SetProperty(ref _recognizedText, value); }
    public string FloatingText { get => _floatingText; set => SetProperty(ref _floatingText, value); }
    public bool IsTextInjectionEnabled { get => _isTextInjectionEnabled; set => SetProperty(ref _isTextInjectionEnabled, value); }

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
    public string RecordingIndicator => IsRecording ? "🔴 Recording..." : "⚪ Idle";

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenModelDownloaderCommand { get; }

    // ── Hotkey ──────────────────────────────────────

    public void RegisterHotkey(nint hwnd)
    {
        var hotkey = _settings.ToggleHotkey;
        if (!string.IsNullOrEmpty(hotkey))
            GlobalHotkeyService.Register(hwnd, hotkey);
    }

    // ── Commands ────────────────────────────────────

    public void Toggle()
    {
        if (IsRecording) Stop();
        else Start();
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
        _settings = SettingsService.Load();

        StatusText = "Initializing engine...";
        RecognizedText = "";
        FloatingText = "";
        _lastInjectedLength = 0;
        lock (_partialResultGate)
        {
            _pendingPartialText = null;
            _hasPendingPartial = false;
        }

        try
        {
            // Compute ModelPath from root + selection if not set directly
            if (string.IsNullOrEmpty(_settings.ModelPath) && !string.IsNullOrEmpty(_settings.ModelsRootPath))
                _settings.ModelPath = Path.Combine(_settings.ModelsRootPath, _settings.SelectedModel);

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
            _settings = settingsWindow.ResultSettings;
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
            if (IsTextInjectionEnabled && text.Length > _lastInjectedLength)
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
