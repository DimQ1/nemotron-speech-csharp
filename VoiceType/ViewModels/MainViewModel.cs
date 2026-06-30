using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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
    }

    // ── Properties ──────────────────────────────────

    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public string RecognizedText { get => _recognizedText; set { _recognizedText = value; OnPropertyChanged(); } }
    public string FloatingText { get => _floatingText; set { _floatingText = value; OnPropertyChanged(); } }

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            _isRecording = value;
            OnPropertyChanged();
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
        if (window.WasDownloaded && window.ResultPath is not null)
        {
            _settings.ModelPath = window.ResultPath;
            SettingsService.Save(_settings);
        }
    }

    private void Start()
    {
        if (IsRecording) return;
        _settings = SettingsService.Load();

        StatusText = "Initializing engine...";
        _recognizedText = "";
        _floatingText = "";
        _lastInjectedLength = 0;
        OnPropertyChanged(nameof(RecognizedText));
        OnPropertyChanged(nameof(FloatingText));

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
            StatusText = "Listening...";

            Console.WriteLine("[VoiceType] Starting recognition...");
            _recognition.Start(_settings);
            Console.WriteLine("[VoiceType] Recognition started OK");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoiceType] Start error: {ex}");
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
            Application.Current.Dispatcher.Invoke(() => Stop());
        }
    }

    private void OnPartialResult(string text)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecognizedText = text;
            FloatingText = text;

            // Inject only new characters since last injection
            if (text.Length > _lastInjectedLength)
            {
                var delta = text[_lastInjectedLength..];
                TextInjector.Inject(delta, _settings.TextInjectionMethod);
                _lastInjectedLength = text.Length;
            }
        });
    }

    private void OnFinalResult(string text)
    {
        Application.Current.Dispatcher.Invoke(async () =>
        {
            RecognizedText = text;
            FloatingText = text;

            // Inject any remaining delta not yet sent
            if (text.Length > _lastInjectedLength)
            {
                var delta = text[_lastInjectedLength..];
                TextInjector.Inject(delta, _settings.TextInjectionMethod);
            }
            _lastInjectedLength = 0;

            IsRecording = false;
            StatusText = "Done";

            // Save session
            if (_currentSession is not null && _settings.SaveSessions)
            {
                _currentSession.EndedAt = DateTime.Now;
                _currentSession.RecognizedText = text;
                _currentSession.IsComplete = true;

                if (_settings.SaveAudioMp3)
                    _currentSession.AudioFilePath = _recognition.SaveAudio(_currentSession.Id);

                SessionManager.SaveSession(_currentSession);
            }
        });
    }

    private void OnRecognitionStopped()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsRecording = false;
            if (StatusText == "Finalizing...")
                StatusText = "Ready";
        });
    }

    // ── INotifyPropertyChanged ──────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
