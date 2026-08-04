using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using SpeechLib.Audio;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.ViewModels;

public sealed partial class AudioMixerViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAudioMixer _mixer;
    private readonly IDisposable? _masterLevelSubscription;
    private readonly IDisposable? _micLevelSubscription;
    private readonly IDisposable? _loopbackLevelSubscription;
    private int _saveVersion;

    // Available capture sources (must match CaptureMode values)
    public IReadOnlyList<string> AudioSourceOptions { get; } = ["Mic", "Loopback", "Mix"];

    [ObservableProperty]
    private float _masterLevel;

    [ObservableProperty]
    private float _micLevel;

    [ObservableProperty]
    private float _loopbackLevel;

    [ObservableProperty]
    private float _micVolume = 1.0f;

    [ObservableProperty]
    private float _loopbackVolume = 1.0f;

    [ObservableProperty]
    private string _audioSource = "Mic";

    public string MicVolumePercent => $"{MicVolume * 100:F0}%";
    public string LoopbackVolumePercent => $"{LoopbackVolume * 100:F0}%";

    public AudioMixerViewModel(ISettingsService settingsService, DispatcherQueue dispatcher, IAudioMixer mixer)
    {
        _settingsService = settingsService;
        _dispatcher = dispatcher;
        _mixer = mixer;

        // Load saved volumes and capture source
        var settings = _settingsService.Load();
        MicVolume = settings.MicVolume;
        LoopbackVolume = settings.LoopbackVolume;
        _audioSource = settings.AudioSource;

        // Apply to audio pipeline
        _mixer.MicVolume = MicVolume;
        _mixer.LoopbackVolume = LoopbackVolume;

        // Subscribe to per-channel + combined level updates
        _masterLevelSubscription = _mixer.LevelMeter.Subscribe(new LevelObserver(this, l => MasterLevel = l));
        _micLevelSubscription = _mixer.MicLevelMeter.Subscribe(new LevelObserver(this, l => MicLevel = l));
        _loopbackLevelSubscription = _mixer.LoopbackLevelMeter.Subscribe(new LevelObserver(this, l => LoopbackLevel = l));
    }

    partial void OnMicVolumeChanged(float value)
    {
        _mixer.MicVolume = value;
        OnPropertyChanged(nameof(MicVolumePercent));
        SaveVolumes();
    }

    partial void OnLoopbackVolumeChanged(float value)
    {
        _mixer.LoopbackVolume = value;
        OnPropertyChanged(nameof(LoopbackVolumePercent));
        SaveVolumes();
    }

    partial void OnAudioSourceChanged(string value)
    {
        // Persist immediately and broadcast so the running capture restarts on the new source.
        var version = Interlocked.Increment(ref _saveVersion);
        _ = Task.Run(() =>
        {
            if (version != Volatile.Read(ref _saveVersion))
                return;

            _settingsService.Update(settings =>
            {
                settings.MicVolume = MicVolume;
                settings.LoopbackVolume = LoopbackVolume;
                settings.AudioSource = value;
            });
            var saved = _settingsService.Load();
            WeakReferenceMessenger.Default.Send(new SettingsSavedMessage(saved));
        });
    }

    private void SaveVolumes()
    {
        var version = Interlocked.Increment(ref _saveVersion);
        var micVolume = MicVolume;
        var loopbackVolume = LoopbackVolume;
        _ = Task.Run(() =>
        {
            if (version != Volatile.Read(ref _saveVersion))
                return;

            _settingsService.Update(settings =>
            {
                settings.MicVolume = micVolume;
                settings.LoopbackVolume = loopbackVolume;
            });
        });
    }

    private sealed class LevelObserver : IAudioLevelObserver
    {
        private readonly AudioMixerViewModel _vm;
        private readonly Action<float> _apply;

        public LevelObserver(AudioMixerViewModel vm, Action<float> apply)
        {
            _vm = vm;
            _apply = apply;
        }

        public void OnAudioLevel(float level)
        {
            _vm._dispatcher.TryEnqueue(() => _apply(level));
        }
    }

    public void Dispose()
    {
        _masterLevelSubscription?.Dispose();
        _micLevelSubscription?.Dispose();
        _loopbackLevelSubscription?.Dispose();
    }
}
