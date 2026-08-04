#if !WINDOWS
using Plugin.Maui.Audio;
using SpeechLib;
using SpeechLib.Models;

namespace LocalVoiceAssistant.Maui.Services;

public sealed class MobileSpeechRecognitionService : ISpeechRecognitionService
{
    private readonly object _sync = new();
    private IAudioStreamer? _streamer;
    private IStreamingSpeechRecognizer? _recognizer;
    private bool _isListening;

    public bool IsSupported => true;
    public string DisplayName => "SpeechLib + MAUI Audio (foreground)";

    public event EventHandler<string>? TranscriptReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    public async Task StartAsync(
        string modelPath,
        string language,
        string executionProvider,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_isListening)
                return;
        }

        var microphone = await Permissions.RequestAsync<Permissions.Microphone>().ConfigureAwait(false);
        if (microphone != PermissionStatus.Granted)
            throw new InvalidOperationException("Разрешение на микрофон не предоставлено.");

        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"Model path not found: {modelPath}");

        var langId = LanguageMapper.Resolve(language);
        var recognizer = new ModelSession(modelPath, executionProvider, langId, useVad: true);
        var streamer = AudioManager.Current.CreateStreamer();
        if (!streamer.CanStreamAudio)
        {
            recognizer.Dispose();
            throw new PlatformNotSupportedException("Платформа не поддерживает streaming audio capture.");
        }

        streamer.OnAudioCaptured += OnAudioCaptured;
        lock (_sync)
        {
            _recognizer = recognizer;
            _streamer = streamer;
            _isListening = true;
        }

#if ANDROID
        global::LocalVoiceAssistant.Maui.Platforms.Android.LvaAudioForegroundService.Start();
#endif
        StatusChanged?.Invoke(this, "Listening in foreground...");
        await streamer.StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IAudioStreamer? streamer;
        lock (_sync)
        {
            if (!_isListening)
                return;
            _isListening = false;
            streamer = _streamer;
            _streamer = null;
        }

        if (streamer is not null)
        {
            streamer.OnAudioCaptured -= OnAudioCaptured;
            await streamer.StopAsync().ConfigureAwait(false);
        }

#if ANDROID
        global::LocalVoiceAssistant.Maui.Platforms.Android.LvaAudioForegroundService.Stop();
#endif
        var recognizer = Interlocked.Exchange(ref _recognizer, null);
        if (recognizer is not null)
        {
            var finalText = recognizer.Flush();
            if (!string.IsNullOrWhiteSpace(finalText))
                TranscriptReceived?.Invoke(this, finalText);
            recognizer.Dispose();
        }

        StatusChanged?.Invoke(this, "Stopped");
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private void OnAudioCaptured(object? sender, AudioStreamEventArgs eventArgs)
    {
        var recognizer = _recognizer;
        if (recognizer is null || eventArgs.Audio.Length < 2)
            return;

        try
        {
            var audio = Pcm16ToFloat(eventArgs.Audio);
            var text = recognizer.ProcessAudio(audio);
            if (!string.IsNullOrWhiteSpace(text))
                TranscriptReceived?.Invoke(this, text);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
        }
    }

    private static float[] Pcm16ToFloat(byte[] bytes)
    {
        var offset = bytes.Length >= 12 &&
                     bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            ? 44
            : 0;
        var sampleCount = Math.Max(0, (bytes.Length - offset) / 2);
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = BitConverter.ToInt16(bytes, offset + (index * 2));
            samples[index] = sample / 32768f;
        }

        return samples;
    }
}
#endif
