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
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models-onnx", ModelSubfolder(settings.ExecutionProvider))
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

        // Warmup: send a silent chunk to prime the model pipeline
        Warmup(_recognizer);

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

        // Cache post-processing settings once — avoid disk I/O on every audio chunk
        var procSettings = SettingsService.Load();
        var procRules = procSettings.PostProcessingRules;
        var procEnabled = procSettings.PostProcessingEnabled;

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
                        _accumulatedText, procRules, procEnabled);
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
            _accumulatedText, procRules, procEnabled);

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

    private static void Warmup(IStreamingSpeechRecognizer recognizer)
    {
        try
        {
            var silent = new float[recognizer.ChunkSamples];
            recognizer.ProcessAudio(silent);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Map execution provider to the matching model subfolder under models-onnx/.</summary>
    private static string ModelSubfolder(string executionProvider) => executionProvider.ToLowerInvariant() switch
    {
        "cuda" => "gpu-cuda",
        "dml" => "gpu-cuda",
        _ => "cpu"
    };
}
