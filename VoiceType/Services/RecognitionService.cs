using System.IO;
using NemotronSpeech;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Wraps <see cref="IStreamingSpeechRecognizer"/> lifecycle and
/// provides a simple high-level API for the UI layer.
/// </summary>
public sealed class RecognitionService : IDisposable
{
    private IStreamingSpeechRecognizer? _recognizer;
    private AudioRecorderService? _audioRecorder;
    private IAudioSource? _audioSource;
    private Thread? _captureThread;
    private ConcurrentQueueWrapper? _buffer;
    private ManualResetEventSlim? _signal;
    private bool _isRunning;
    private string _accumulatedText = "";

    public event Action<string>? PartialResult;
    public event Action<string>? FinalResult;
    public event Action? Stopped;

    public bool IsRunning => _isRunning;
    public int SampleRate => _recognizer?.SampleRate ?? 16000;
    public string AccumulatedText => _accumulatedText;

    public void Initialize(AppSettings settings)
    {
        var modelPath = string.IsNullOrEmpty(settings.ModelPath)
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models-onnx", "cpu")
            : settings.ModelPath;

        if (!Path.IsPathRooted(modelPath))
            modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, modelPath));

        var langId = LanguageMapper.Resolve(settings.Language);

        _recognizer = new ModelSession(modelPath, settings.ExecutionProvider, langId, settings.UseVad);
    }

    public void Start(AppSettings settings)
    {
        if (_recognizer is null) Initialize(settings);
        if (_recognizer is null) throw new InvalidOperationException("Recognizer not initialized.");

        _accumulatedText = "";
        _isRunning = true;

        _audioRecorder = new AudioRecorderService(_recognizer.SampleRate);
        _audioRecorder.Start();

        _audioSource = Transcriber.CreateAudioSource(
            Enum.Parse<CaptureMode>(settings.AudioSource),
            _recognizer.SampleRate);

        _buffer = new ConcurrentQueueWrapper();
        _signal = new ManualResetEventSlim(false);

        _captureThread = new Thread(() =>
        {
            var running = _isRunning;
            _audioSource!.Start(_buffer, _signal!, ref running);
        }) { IsBackground = true };
        _captureThread.Start();

        // Processing loop on thread pool
        Task.Run(() => ProcessLoop());
    }

    public void Stop()
    {
        _isRunning = false;
        _signal?.Set();
    }

    private void ProcessLoop()
    {
        var lastAudio = DateTime.UtcNow;

        while (_isRunning || (_buffer?.IsEmpty == false))
        {
            bool gotData = false;
            while (_buffer?.TryDequeue(out var batch) == true)
            {
                var raw = _recognizer!.ProcessAudio(batch);
                if (raw is not null)
                {
                    _accumulatedText += raw;
                    var processed = PostProcessingPipeline.Process(
                        _accumulatedText,
                        SettingsService.Load().PostProcessingRules,
                        SettingsService.Load().PostProcessingEnabled);
                    PartialResult?.Invoke(processed);
                }
                _audioRecorder?.Append(batch);
                gotData = true;
            }

            if (gotData)
                lastAudio = DateTime.UtcNow;
            else
                Thread.Sleep(1);

            if (!_isRunning && (_buffer?.IsEmpty == true) &&
                (DateTime.UtcNow - lastAudio).TotalSeconds > 1.5)
                break;
        }

        // Flush
        var final = _recognizer!.Flush();
        if (final is not null) _accumulatedText += final;

        var finalProcessed = PostProcessingPipeline.Process(
            _accumulatedText,
            SettingsService.Load().PostProcessingRules,
            SettingsService.Load().PostProcessingEnabled);

        FinalResult?.Invoke(finalProcessed);
        Stopped?.Invoke();

        _audioSource?.Dispose();
    }

    public string? SaveAudio(string sessionId)
    {
        if (_audioRecorder is null) return null;
        var dir = SessionManager.EnsureDirectory();
        var path = Path.Combine(dir, $"{sessionId}_audio");
        return _audioRecorder.StopAndSave(path);
    }

    public void Dispose()
    {
        _isRunning = false;
        _recognizer?.Dispose();
        _audioRecorder?.Dispose();
        _audioSource?.Dispose();
    }
}
