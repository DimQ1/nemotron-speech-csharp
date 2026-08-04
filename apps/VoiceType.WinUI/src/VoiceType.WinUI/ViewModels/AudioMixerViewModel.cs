using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using SpeechLib.Audio;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.ViewModels;

public sealed partial class AudioMixerViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAudioMixer _mixer;
    private readonly IDisposable? _audioLevelSubscription;
    private int _saveVersion;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private float _micVolume = 1.0f;

    [ObservableProperty]
    private float _loopbackVolume = 1.0f;

    public string MicVolumePercent => $"{MicVolume * 100:F0}%";
    public string LoopbackVolumePercent => $"{LoopbackVolume * 100:F0}%";

    public AudioMixerViewModel(ISettingsService settingsService, DispatcherQueue dispatcher, IAudioMixer mixer)
    {
        _settingsService = settingsService;
        _dispatcher = dispatcher;
        _mixer = mixer;

        // Load saved volumes
        var settings = _settingsService.Load();
        MicVolume = settings.MicVolume;
        LoopbackVolume = settings.LoopbackVolume;

        // Apply to audio pipeline
        _mixer.MicVolume = MicVolume;
        _mixer.LoopbackVolume = LoopbackVolume;

        // Subscribe to audio level updates
        _audioLevelSubscription = _mixer.LevelMeter.Subscribe(
            new AudioLevelObserver(this));
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

    private sealed class AudioLevelObserver : IAudioLevelObserver
    {
        private readonly AudioMixerViewModel _vm;
        public AudioLevelObserver(AudioMixerViewModel vm) => _vm = vm;
        public void OnAudioLevel(float level)
        {
            _vm._dispatcher.TryEnqueue(() => _vm.AudioLevel = level);
        }
    }

    public void Dispose()
    {
        _audioLevelSubscription?.Dispose();
    }
}
