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
    private readonly IDisposable? _audioLevelSubscription;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private float _micVolume = 1.0f;

    [ObservableProperty]
    private float _loopbackVolume = 1.0f;

    public string MicVolumePercent => $"{MicVolume * 100:F0}%";
    public string LoopbackVolumePercent => $"{LoopbackVolume * 100:F0}%";

    public AudioMixerViewModel(ISettingsService settingsService, DispatcherQueue dispatcher)
    {
        _settingsService = settingsService;
        _dispatcher = dispatcher;

        // Load saved volumes
        var settings = _settingsService.Load();
        MicVolume = settings.MicVolume;
        LoopbackVolume = settings.LoopbackVolume;

        // Apply to audio pipeline
        NAudio3AudioSource.MicVolume = MicVolume;
        NAudio3AudioSource.LoopbackVolume = LoopbackVolume;

        // Subscribe to audio level updates
        _audioLevelSubscription = NAudio3AudioSource.AudioLevelMeter.Subscribe(
            new AudioLevelObserver(this));
    }

    partial void OnMicVolumeChanged(float value)
    {
        NAudio3AudioSource.MicVolume = value;
        OnPropertyChanged(nameof(MicVolumePercent));
        SaveVolumes();
    }

    partial void OnLoopbackVolumeChanged(float value)
    {
        NAudio3AudioSource.LoopbackVolume = value;
        OnPropertyChanged(nameof(LoopbackVolumePercent));
        SaveVolumes();
    }

    private void SaveVolumes()
    {
        var settings = _settingsService.Load();
        settings.MicVolume = MicVolume;
        settings.LoopbackVolume = LoopbackVolume;
        _settingsService.Save(settings);
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
