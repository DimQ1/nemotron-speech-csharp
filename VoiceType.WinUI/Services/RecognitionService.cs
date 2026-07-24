using System.IO;
using System.Text;
using CommonUtils;
using NemotronSpeech;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Wraps <see cref="IStreamingSpeechRecognizer"/> lifecycle and
/// provides a simple high-level API for the UI layer.
///
/// Model lifecycle is separated from capture lifecycle:
/// - <see cref="LoadModelAsync"/> loads ONNX model into memory (slow, one-time)
/// - <see cref="Start"/> / <see cref="Stop"/> control audio capture only
/// - Model stays loaded across multiple Start/Stop cycles
/// </summary>
public sealed class RecognitionService : IRecognitionService
{
    private readonly ISettingsService? _settingsService;
    private readonly IPostProcessingPipeline? _postProcessing;
    private readonly ISessionManager? _sessionManager;
    private readonly ISystemTelemetry? _telemetry;

    private IStreamingSpeechRecognizer? _recognizer;
    private AudioRecorderService? _audioRecorder;
    private IAudioSource? _audioSource;
    private Thread? _captureThread;
    private ConcurrentQueueWrapper? _buffer;
    private ManualResetEventSlim? _signal;
    private CaptureState? _captureState;
    private bool _isRunning;
    private volatile bool _captureMuted;
    private Task? _processTask;
    private readonly StringBuilder _accumulatedText = new();
    private readonly StringBuilder _partialProcessedText = new();

    private ModelState _modelState = ModelState.Unloaded;
    private CancellationTokenSource? _modelLoadCts;
    private readonly object _modelGate = new();

    public RecognitionService() { }

    public RecognitionService(
        ISettingsService settingsService,
        IPostProcessingPipeline postProcessing,
        ISessionManager sessionManager,
        ISystemTelemetry? telemetry = null)
    {
        _settingsService = settingsService;
        _postProcessing = postProcessing;
        _sessionManager = sessionManager;
        _telemetry = telemetry ?? App.Telemetry;
    }

    public event Action<string>? PartialResult;
    public event Action<string>? FinalResult;
    public event Action? Stopped;
    public event Action<ModelState>? ModelStateChanged;

    public bool IsRunning => _isRunning;
    public bool IsMuted => _captureMuted;
    public int SampleRate => _recognizer?.SampleRate ?? 16000;
    public string AccumulatedText => _accumulatedText.ToString();

    public ModelState ModelState
    {
        get => _modelState;
        private set
        {
            if (_modelState == value) return;
            _modelState = value;
            ModelStateChanged?.Invoke(value);
        }
    }

    // ── Model lifecycle ────────────────────────────────────────

    public async Task LoadModelAsync(AppSettings settings)
    {
        lock (_modelGate)
        {
            if (_modelState is ModelState.Loading or ModelState.Loaded)
                return; // Already loading or loaded — no-op

            ModelState = ModelState.Loading;
        }

        // Cancel any in-flight load
        _modelLoadCts?.Cancel();
        _modelLoadCts = new CancellationTokenSource();
        var ct = _modelLoadCts.Token;

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var modelPath = ResolveModelPath(settings);

                var langId = LanguageMapper.Resolve(settings.Language);

                var searchOptions = new GeneratorParamsArgs
                {
                    num_beams = settings.NumBeams,
                    do_sample = false,
                    repetition_penalty = settings.RepetitionPenalty
                };

                var newRecognizer = new ModelSession(modelPath, settings.ExecutionProvider, langId, settings.UseVad, searchOptions);

                // Atomically swap recognizers
                var old = Interlocked.Exchange(ref _recognizer, newRecognizer);
                old?.Dispose();
            }, ct);

            ModelState = ModelState.Loaded;
            _telemetry?.LogInfo("Recognition", "Model loaded successfully");
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a newer LoadModelAsync call — don't change state
        }
        catch (Exception ex)
        {
            ModelState = ModelState.Error;
            _telemetry?.LogError("Recognition", $"Model load failed: {ex.Message}", ex);
        }
    }

    public void UnloadModel()
    {
        lock (_modelGate)
        {
            if (_modelState == ModelState.Unloaded) return;

            _modelLoadCts?.Cancel();
            _modelLoadCts = null;

            var old = Interlocked.Exchange(ref _recognizer, null);
            old?.Dispose();

            ModelState = ModelState.Unloaded;
            _telemetry?.LogInfo("Recognition", "Model unloaded");
        }
    }

    // ── Capture lifecycle ──────────────────────────────────────

    public void Start(AppSettings settings)
    {
        if (_recognizer is null)
            throw new InvalidOperationException("Model is not loaded. Call LoadModelAsync first.");

        // Dispose previous session resources before creating new ones
        CleanupPreviousSession();

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

        // Processing loop on thread pool — track task to await on restart
        _processTask = Task.Run(async () => await ProcessLoop().ConfigureAwait(false));
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
        _telemetry?.LogInfo("Recognition", $"Capture {(muted ? "muted" : "unmuted")}");
    }

    private async Task ProcessLoop()
    {
        var lastAudio = DateTime.UtcNow;

        // Cache post-processing settings once � avoid disk I/O on every audio chunk
        var procSettings = (_settingsService ?? new SettingsService()).Load();
        var procRules = procSettings.PostProcessingRules;
        var procEnabled = procSettings.PostProcessingEnabled;
        var postProc = _postProcessing ?? new PostProcessingPipeline();
        var compiledProcRules = postProc.CompileRules(procRules, procEnabled);

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

                if (_audioRecorder is not null)
                    await _audioRecorder.AppendAsync(batch).ConfigureAwait(false);

                var raw = _recognizer!.ProcessAudio(batch);
                if (raw is not null)
                {
                    _accumulatedText.Append(raw);
                    var processedDelta = postProc.Process(raw, compiledProcRules);
                    if (!string.IsNullOrEmpty(processedDelta))
                        _partialProcessedText.Append(processedDelta);

                    PartialResult?.Invoke(_partialProcessedText.ToString());
                }
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

        var finalProcessed = postProc.ProcessFinal(_accumulatedText.ToString(), compiledProcRules);

        FinalResult?.Invoke(finalProcessed);
        Stopped?.Invoke();

        _audioSource?.Dispose();
    }

    public string? SaveAudio(string fileNameBase)
    {
        if (_audioRecorder is null) return null;
        var sessionMgr = _sessionManager ?? new SessionManager();
        var dir = sessionMgr.EnsureDirectory();
        var path = Path.Combine(dir, fileNameBase);
        return _audioRecorder.StopAndSave(path);
    }

    public void Dispose()
    {
        _isRunning = false;
        CleanupPreviousSession();
        UnloadModel();
    }

    /// <summary>
    /// Dispose all per-session resources (recorder, audio source, sync primitives)
    /// to prevent memory/file-handle leaks across recognition restarts.
    /// </summary>
    private void CleanupPreviousSession()
    {
        if (_captureState is not null)
            _captureState.IsRunning = false;
        _signal?.Set();

        // Wait for the previous ProcessLoop task to fully complete
        // before disposing its resources — prevents use-after-dispose.
        if (_processTask is not null)
        {
            try { _processTask.GetAwaiter().GetResult(); } catch { }
            _processTask = null;
        }

        _audioRecorder?.Dispose();
        _audioRecorder = null;

        _audioSource?.Dispose();
        _audioSource = null;

        _signal?.Dispose();
        _signal = null;

        _buffer = null;
        _captureState = null;
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

    /// <summary>Resolve the model path from settings, falling back to default if empty.</summary>
    private static string ResolveModelPath(AppSettings settings)
    {
        var modelPath = string.IsNullOrEmpty(settings.ModelPath)
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "modules", "asr", ModelSubfolder(settings.ExecutionProvider))
            : settings.ModelPath;

        if (!Path.IsPathRooted(modelPath))
            modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, modelPath));

        return modelPath;
    }

    /// <summary>Map execution provider to the matching model subfolder.</summary>
    private static string ModelSubfolder(string executionProvider) => executionProvider.ToLowerInvariant() switch
    {
        "cuda" => "gpu-cuda",
        "dml" => "gpu-cuda",
        _ => "cpu"
    };
}
