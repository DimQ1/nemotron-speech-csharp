using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using VoiceType.Hotkeys;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Platform;

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
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IPlatformTextInjector _textInjector;
    private readonly DispatcherQueue _dispatcher;

    private AppSettings _settings;
    private int _toggleHotkeyId;

    public MainViewModel(
        RecognitionService recognition,
        SettingsService settingsService,
        IGlobalHotkeyService hotkeys,
        IPlatformTextInjector textInjector)
    {
        _recognition = recognition;
        _settingsService = settingsService;
        _hotkeys = hotkeys;
        _textInjector = textInjector;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _settings = settingsService.Load();
        _selectedLanguage = _settings.Language;
        IsTextInjectionEnabled = _settings.IsTextInjectionEnabled;
        IsAutoScrollEnabled = _settings.IsAutoScrollEnabled;

        _recognition.PartialResult += text => _dispatcher.TryEnqueue(() => FloatingText = text);
        _recognition.FinalResult += text => _dispatcher.TryEnqueue(() =>
        {
            FloatingText = text;
            if (IsTextInjectionEnabled && !string.IsNullOrEmpty(text))
                _textInjector.Inject(text);
        });
        _recognition.Stopped += () => _dispatcher.TryEnqueue(() =>
        {
            IsRecording = false;
            StatusText = "Ready";
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

        // Global hotkeys: portal grants bindings asynchronously (consent dialog
        // on first run). Registration happens in the background; presses arrive
        // via the HotkeyPressed event.
        _hotkeys.HotkeyPressed += id =>
        {
            if (id == _toggleHotkeyId)
                _dispatcher.TryEnqueue(() => _ = ToggleAsync());
        };

        if (_hotkeys.IsAvailable && !string.IsNullOrWhiteSpace(_settings.ToggleHotkey))
            _ = RegisterToggleHotkeyAsync(_settings.ToggleHotkey);
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
    private bool _isModelReady;

    [ObservableProperty]
    private string _modelStatusText = "No model loaded";

    [ObservableProperty]
    private bool _isTextInjectionEnabled;

    [ObservableProperty]
    private bool _isAutoScrollEnabled;

    [ObservableProperty]
    private string _selectedLanguage;

    public string RecordButtonText => IsModelLoading
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

        if (IsModelLoading)
            return;

        if (_recognition.ModelState != ModelLifecycleState.Loaded)
        {
            StatusText = "Loading model...";
            try
            {
                await _recognition.LoadModelAsync(_settings);
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

    partial void OnIsTextInjectionEnabledChanged(bool value) =>
        _settingsService.Update(s => s.IsTextInjectionEnabled = value);

    partial void OnSelectedLanguageChanged(string value)
    {
        _settings.Language = value;
        _settingsService.Update(s => s.Language = value);
    }
}
