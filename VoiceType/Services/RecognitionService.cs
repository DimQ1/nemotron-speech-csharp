using System.IO;
using System.Text;
using CommonUtils;
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
    private CaptureState? _captureState;
    private bool _isRunning;
    private volatile bool _captureMuted;
    private readonly StringBuilder _accumulatedText = new();
    private readonly StringBuilder _partialProcessedText = new();

    public event Action<string>? PartialResult;
    public event Action<string>? FinalResult;
    public event Action? Stopped;

    public bool IsRunning => _isRunning;
    public bool IsMuted => _captureMuted;
    public int SampleRate => _recognizer?.SampleRate ?? 16000;
    public string AccumulatedText => _accumulatedText.ToString();

    public void Initialize(AppSettings settings)
    {
        var modelPath = string.IsNullOrEmpty(settings.ModelPath)
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models-onnx", ModelSubfolder(settings.ExecutionProvider))
            : settings.ModelPath;

        if (!Path.IsPathRooted(modelPath))
            modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, modelPath));

        var langId = LanguageMapper.Resolve(settings.Language);

        var searchOptions = new GeneratorParamsArgs
        {
            num_beams = settings.NumBeams,
            do_sample = false,
            repetition_penalty = settings.RepetitionPenalty
        };

        _recognizer = new ModelSession(modelPath, settings.ExecutionProvider, langId, settings.UseVad, searchOptions);
    }

    public void Start(AppSettings settings)
    {
        if (_recognizer is null) Initialize(settings);
        if (_recognizer is null) throw new InvalidOperationException("Recognizer not initialized.");

        _accumulatedText.Clear();
        _partialProcessedText.Clear();
        _isRunning = true;

        _audioRecorder = new AudioRecorderService(_recognizer.SampleRate);
        _audioRecorder.Start();

        _audioSource = Transcriber.CreateAudioSource(
            Enum.Parse<CaptureMode>(settings.AudioSource),
            _recognizer.SampleRate);

        _buffer = new ConcurrentQueueWrapper();
        _signal = new ManualResetEventSlim(false);
        _captureState = new CaptureState();

        // Warmup: send a silent chunk to prime the model pipeline
        Warmup(_recognizer);

        _captureThread = new Thread(() =>
        {
            _audioSource!.Start(_buffer, _signal!, _captureState);
        }) { IsBackground = true };
        _captureThread.Start();

        // Processing loop on thread pool
        Task.Run(() => ProcessLoop());
    }

    public void Stop()
    {
        _isRunning = false;
        if (_captureState is not null)
            _captureState.IsRunning = false;
        _signal?.Set();
    }

    /// <summary>Mute/unmute capture. When muted, audio is discarded without recognition (saves CPU).</summary>
    public void SetMuted(bool muted)
    {
        _captureMuted = muted;
        Console.WriteLine($"[VoiceType] Capture {(muted ? "muted" : "unmuted")}");
    }

    private void ProcessLoop()
    {
        var lastAudio = DateTime.UtcNow;

        // Cache post-processing settings once � avoid disk I/O on every audio chunk
        var procSettings = SettingsService.Load();
        var procRules = procSettings.PostProcessingRules;
        var procEnabled = procSettings.PostProcessingEnabled;
        var compiledProcRules = PostProcessingPipeline.CompileRules(procRules, procEnabled);

        while ((_captureState?.IsRunning == true) || (_buffer?.IsEmpty == false))
        {
            bool gotData = false;
            while (_buffer?.TryDequeue(out var batch) == true)
            {
                if (_captureMuted)
                {
                    // Muted: discard audio, no recognition (saves CPU)
                    gotData = true;
                    continue;
                }

                var raw = _recognizer!.ProcessAudio(batch);
                if (raw is not null)
                {
                    _accumulatedText.Append(raw);
                    var processedDelta = PostProcessingPipeline.Process(raw, compiledProcRules);
                    if (!string.IsNullOrEmpty(processedDelta))
                        _partialProcessedText.Append(processedDelta);

                    PartialResult?.Invoke(_partialProcessedText.ToString());
                }
                _audioRecorder?.Append(batch);
                gotData = true;
            }

            if (gotData)
                lastAudio = DateTime.UtcNow;
            else
            {
                _signal?.Wait(50);
                _signal?.Reset();
            }

            if ((_captureState?.IsRunning != true) && (_buffer?.IsEmpty == true) &&
                (DateTime.UtcNow - lastAudio).TotalSeconds > 1.5)
                break;
        }

        // Flush
        var final = _recognizer!.Flush();
        if (final is not null) _accumulatedText.Append(final);

        var finalProcessed = PostProcessingPipeline.Process(_accumulatedText.ToString(), compiledProcRules);

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
        if (_captureState is not null)
            _captureState.IsRunning = false;
        _recognizer?.Dispose();
        _audioRecorder?.Dispose();
        _audioSource?.Dispose();
        _signal?.Set();
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
