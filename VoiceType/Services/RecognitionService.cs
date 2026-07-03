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
    private RecognizerConfiguration? _recognizerConfiguration;
    private AudioRecorderService? _audioRecorder;
    private IAudioSource? _audioSource;
    private Thread? _captureThread;
    private ConcurrentQueueWrapper? _buffer;
    private ManualResetEventSlim? _signal;
    private CaptureState? _captureState;
    private bool _isRunning;
    private readonly StringBuilder _accumulatedText = new();
    private readonly StringBuilder _partialProcessedText = new();

    public event Action<string>? PartialResult;
    public event Action<string>? FinalResult;
    public event Action? Stopped;

    public bool IsRunning => _isRunning;
    public int SampleRate => _recognizer?.SampleRate ?? 16000;
    public string AccumulatedText => _accumulatedText.ToString();

    public void Initialize(AppSettings settings)
    {
        var configuration = CreateRecognizerConfiguration(settings);
        if (_recognizer is not null && _recognizerConfiguration == configuration)
            return;

        var searchOptions = new GeneratorParamsArgs
        {
            num_beams = configuration.NumBeams,
            do_sample = false,
            repetition_penalty = configuration.RepetitionPenalty
        };

        _recognizer?.Dispose();
        _recognizer = new ModelSession(
            configuration.ModelPath,
            configuration.ExecutionProvider,
            configuration.LanguageId,
            configuration.UseVad,
            searchOptions);
        _recognizerConfiguration = configuration;
    }

    public void Start(AppSettings settings)
    {
        if (_recognizer is null) Initialize(settings);
        if (_recognizer is null) throw new InvalidOperationException("Recognizer not initialized.");

        _accumulatedText.Clear();
        _partialProcessedText.Clear();
        _isRunning = true;

        _audioRecorder?.Dispose();
        _audioRecorder = null;
        if (settings.SaveSessions && settings.SaveAudioMp3)
        {
            _audioRecorder = new AudioRecorderService(_recognizer.SampleRate);
            _audioRecorder.Start();
        }

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

        var compiledProcRules = PostProcessingPipeline.CompileRules(
            settings.PostProcessingRules,
            settings.PostProcessingEnabled);

        // Processing loop on thread pool
        Task.Run(() => ProcessLoop(compiledProcRules));
    }

    public void Stop()
    {
        _isRunning = false;
        if (_captureState is not null)
            _captureState.IsRunning = false;
        _signal?.Set();
    }

    private void ProcessLoop(IReadOnlyList<PostProcessingPipeline.CompiledRule> compiledProcRules)
    {
        var lastAudio = DateTime.UtcNow;

        while ((_captureState?.IsRunning == true) || (_buffer?.IsEmpty == false))
        {
            bool gotData = false;
            while (_buffer?.TryDequeue(out var batch) == true)
            {
                var raw = _recognizer!.ProcessAudio(batch);
                if (raw is not null)
                {
                    _accumulatedText.Append(raw);
                    var processedDelta = PostProcessingPipeline.Process(raw, compiledProcRules);
                    if (!string.IsNullOrEmpty(processedDelta))
                    {
                        _partialProcessedText.Append(processedDelta);
                        PartialResult?.Invoke(_partialProcessedText.ToString());
                    }
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
        _recognizer = null;
        _recognizerConfiguration = null;
        _audioRecorder?.Dispose();
        _audioSource?.Dispose();
        _signal?.Set();
    }

    internal static RecognizerConfiguration CreateRecognizerConfiguration(AppSettings settings)
    {
        var modelPath = string.IsNullOrEmpty(settings.ModelPath)
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models-onnx", ModelSubfolder(settings.ExecutionProvider))
            : settings.ModelPath;

        if (!Path.IsPathRooted(modelPath))
            modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, modelPath));

        return new RecognizerConfiguration(
            modelPath,
            settings.ExecutionProvider,
            LanguageMapper.Resolve(settings.Language),
            settings.UseVad,
            settings.NumBeams,
            settings.RepetitionPenalty);
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

    internal readonly record struct RecognizerConfiguration(
        string ModelPath,
        string ExecutionProvider,
        string? LanguageId,
        bool UseVad,
        int NumBeams,
        double RepetitionPenalty);
}
