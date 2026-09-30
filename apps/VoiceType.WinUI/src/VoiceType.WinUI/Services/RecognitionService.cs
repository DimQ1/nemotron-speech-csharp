using System.IO;
using System.Text;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Decorators;
using SpeechLib.Models;
using SpeechLib.Providers;
using SpeechLib.Recognition;
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
    private readonly IAudioSourceFactory _audioSourceFactory;
    private readonly IAudioRecorderFactory _audioRecorderFactory;
    private readonly IAppPaths _appPaths;

    private IStreamingSpeechRecognizer? _recognizer;
    private IAudioRecorder? _audioRecorder;
    private IAudioSource? _audioSource;
    private Thread? _captureThread;
    private ConcurrentQueueWrapper? _buffer;
    private ManualResetEventSlim? _signal;
    private CaptureState? _captureState;
    private bool _isRunning;
    private volatile bool _captureMuted;
    private volatile Exception? _captureError;
    private Task? _processTask;
    private readonly StringBuilder _accumulatedText = new();
    private readonly StringBuilder _partialProcessedText = new();
    private readonly object _recognizerOperationGate = new();
    private RuntimeProcessingSettings _processingSettings = RuntimeProcessingSettings.Empty;
    private string? _loadedModelPath;

    private ModelState _modelState = ModelState.Unloaded;
    private CancellationTokenSource? _modelLoadCts;
    private readonly object _modelGate = new();

    public RecognitionService(
        ISettingsService settingsService,
        IPostProcessingPipeline postProcessing,
        ISessionManager sessionManager,
        IAudioSourceFactory audioSourceFactory,
        ISystemTelemetry? telemetry = null,
        IAudioRecorderFactory? audioRecorderFactory = null,
        IAppPaths? appPaths = null)
    {
        _settingsService = settingsService;
        _postProcessing = postProcessing;
        _sessionManager = sessionManager;
        _audioSourceFactory = audioSourceFactory;
        _audioRecorderFactory = audioRecorderFactory ?? new NAudio3AudioRecorderFactory();
        _appPaths = appPaths ?? new AppPathsAdapter();
        _telemetry = telemetry ?? App.Telemetry;
    }

    public event Action<string>? PartialResult;
    public event Action<string>? CommittedResult;
    public event Action<string>? FinalResult;
    public event Action<string>? UtteranceFinalized;
    public event Action? Stopped;

    /// <summary>Fires when audio capture fails to start (missing microphone, broken loopback, etc.).</summary>
    public event Action<string>? CaptureError;

    public event Action<ModelState>? ModelStateChanged;

    public bool IsRunning => _isRunning;
    public bool IsMuted => _captureMuted;
    public int SampleRate => _recognizer?.SampleRate ?? 16000;
    public string AccumulatedText => _accumulatedText.ToString();
    public string? LoadedModelPath => Volatile.Read(ref _loadedModelPath);
    public bool SupportsPromptTranslation => _recognizer is ITranslationConfigurable;
    public bool SupportsLanguageSelection => _recognizer is ILanguageConfigurable;

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

        var loadedModelPath = string.Empty;
        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var modelPath = ResolveModelPath(settings);
            loadedModelPath = modelPath;

                // Build the recognizer through the shared factory: it selects the
                // provider (Nemotron GenAI vs Parakeet TDT) and applies the shared
                // decorators (Silero VAD gate, metrics).
                IStreamingSpeechRecognizer newRecognizer = RecognizerFactory.Create(new RecognizerFactoryOptions
                {
                    ModelPath = modelPath,
                    ExecutionProvider = settings.ExecutionProvider,
                    Language = settings.Language,
                    TranslationEnabled = settings.TranslationEnabled,
                    TranslationLanguage = settings.TranslationTargetLanguage,
                    UseVad = settings.UseVad,
                    RepetitionPenalty = settings.RepetitionPenalty,
                    SileroVadPath = _appPaths.SileroVadPath,
                });

                // Atomically swap recognizers
                var old = Interlocked.Exchange(ref _recognizer, newRecognizer);
                lock (_recognizerOperationGate)
                    old?.Dispose();
            }, ct);

            ModelState = ModelState.Loaded;
            Volatile.Write(ref _loadedModelPath, loadedModelPath);
            _telemetry?.LogInfo("Recognition", "Model loaded successfully");
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a newer LoadModelAsync call — don't change state
        }
        catch (Exception ex)
        {
            ModelState = ModelState.Error;
            Volatile.Write(ref _loadedModelPath, null);
            _telemetry?.LogError("Recognition", $"Model load failed: {ex.Message}", ex);
        }
    }

    public void UnloadModel()
    {
        // If recognition is running, stop it first to prevent ObjectDisposedException
        // in ProcessLoop when the recognizer is swapped out.
        if (_isRunning)
        {
            _telemetry?.LogInfo("Recognition", "Stopping recognition before model unload");
            Stop();

            // Give ProcessLoop a moment to finish gracefully
            if (_processTask is not null)
            {
                try { _processTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
                _processTask = null;
            }
        }

        lock (_modelGate)
        {
            if (_modelState == ModelState.Unloaded) return;

            _modelLoadCts?.Cancel();
            _modelLoadCts = null;

            var old = Interlocked.Exchange(ref _recognizer, null);
            lock (_recognizerOperationGate)
                old?.Dispose();

            Volatile.Write(ref _loadedModelPath, null);
            ModelState = ModelState.Unloaded;
            _telemetry?.LogInfo("Recognition", "Model unloaded");
        }
    }

    // ── Capture lifecycle ──────────────────────────────────────

    /// <summary>
    /// Starts capture for a new session.
    /// A previous session is shut down first (await, not block): the decode loop and
    /// the capture thread share the session fields, so the cleanup has to complete
    /// before the new session claims them.
    /// </summary>
    public async Task StartAsync(AppSettings settings)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RecognitionService));

        if (_recognizer is null || _modelState != ModelState.Loaded)
            throw new InvalidOperationException("Model is not loaded. Call LoadModelAsync first.");

        ApplyRuntimeSettings(settings);

        // Release the previous session's resources before creating new ones.
        await StopAndCleanupAsync().ConfigureAwait(false);

        _accumulatedText.Clear();
        _partialProcessedText.Clear();
        _isRunning = true;
        _captureError = null;

        if (settings.SaveAudioMp3)
        {
            _audioRecorder = _audioRecorderFactory.Create(_recognizer.SampleRate);
            _audioRecorder.Start(_appPaths.EnsureTempDir());
        }

        _audioSource = _audioSourceFactory.Create(
            Enum.Parse<CaptureMode>(settings.AudioSource),
            _recognizer.SampleRate);

        _buffer = new ConcurrentQueueWrapper();
        _signal = new ManualResetEventSlim(false);
        _captureState = new CaptureState();

        // Reset streaming state, then send a silent chunk to prime the model
        // pipeline. ResetStreamingState clears buffered audio/decoder state left
        // over from a previous session (Parakeet TDT buffers audio between chunks).
        lock (_recognizerOperationGate)
        {
            _recognizer.ResetStreamingState();
            Warmup(_recognizer);
        }

        _captureThread = new Thread(() =>
        {
            try
            {
                _audioSource!.Start(_buffer, _signal!, _captureState);
            }
            catch (Exception ex)
            {
                // Record the failure so ProcessLoop can surface it instead of hanging
                // in the "listening" state forever (thread would otherwise die silently).
                _captureError = ex;
            }
            finally
            {
                // Guarantee ProcessLoop's wait condition can observe the failed capture
                // and terminate even though _isRunning is still true.
                _captureState?.Stop();
                _signal?.Set();
            }
        }) { IsBackground = true, Name = "VoiceType-capture" };
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

    /// <summary>Full cleanup: stop recognition, wait for ProcessLoop to finish, and reset state.
    /// Call this before unloading the model to ensure a clean restart later.</summary>
    public async Task StopAndCleanupAsync()
    {
        Stop();

        // Wait for ProcessLoop to complete (max 2 seconds)
        if (_processTask is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _processTask.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) { /* best-effort */ }
            catch { /* ProcessLoop may throw on disposal race */ }
            _processTask = null;
        }

        CleanupPreviousSession();
    }

    /// <summary>Mute/unmute capture. When muted, audio is discarded without recognition (saves CPU).</summary>
    public void SetMuted(bool muted)
    {
        _captureMuted = muted;
        _telemetry?.LogInfo("Recognition", $"Capture {(muted ? "muted" : "unmuted")}");
    }

    public void ApplyRuntimeSettings(AppSettings settings)
    {
        var postProcessing = _postProcessing ?? new PostProcessingPipeline();
        var rules = settings.PostProcessingRules
            .Select(rule => new PostProcessingRule
            {
                Name = rule.Name,
                Pattern = rule.Pattern,
                Replacement = rule.Replacement,
                Enabled = rule.Enabled
            })
            .ToList();
        var compiledRules = postProcessing.CompileRules(rules, settings.PostProcessingEnabled);
        Volatile.Write(ref _processingSettings, new RuntimeProcessingSettings(compiledRules));

        lock (_recognizerOperationGate)
        {
            if (_recognizer is IRuntimeConfigurable runtimeConfigurable)
            {
                runtimeConfigurable.TrySetVad(settings.UseVad);
                runtimeConfigurable.TrySetSearchOptions(1, settings.RepetitionPenalty);
            }

            if (_recognizer is ILanguageConfigurable languageConfigurable)
            {
                if (!string.IsNullOrWhiteSpace(settings.Language))
                    languageConfigurable.TrySetLanguage(settings.Language);
            }

            if (_recognizer is ITranslationConfigurable translationConfigurable)
            {
                translationConfigurable.TrySetTranslation(
                    settings.TranslationEnabled,
                    settings.TranslationTargetLanguage);
            }
        }
    }

    private async Task ProcessLoop()
    {
        var postProc = _postProcessing ?? new PostProcessingPipeline();

        // Run while the user session is active, then drain whatever audio remains.
        // We key off _isRunning (the explicit user/session flag) rather than a silence
        // timeout: on FIRST launch the WASAPI loopback device can take a couple of seconds
        // to spin up (and the system may simply be silent), so the old 1.5s-silence break
        // aborted ProcessLoop before any audio arrived and fired Stopped — recognition never
        // started until the app was restarted. Now silence just keeps the loop waiting.
         while ((_isRunning && _captureState?.IsRunning == true) ||
             (_captureThread?.IsAlive == true) ||
             (_buffer?.IsEmpty == false))
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

                if (_recognizer is IUtteranceStreamingRecognizer utteranceRecognizer)
                {
                    StreamingResult result;
                    lock (_recognizerOperationGate)
                        result = utteranceRecognizer.ProcessUtterance(batch);
                    HandleStreamingResult(postProc, result);
                }
                else
                {
                    string? raw;
                    lock (_recognizerOperationGate)
                        raw = _recognizer!.ProcessAudio(batch);

                    var processingSettings = Volatile.Read(ref _processingSettings);
                    if (raw is not null)
                    {
                        _accumulatedText.Append(raw);
                        var processedDelta = postProc.Process(raw, processingSettings.CompiledRules);
                        if (!string.IsNullOrEmpty(processedDelta))
                            _partialProcessedText.Append(processedDelta);
                    }

                    // The recognizer's PartialText is revisable and never committed: show
                    // committed + provisional so live text appears early while the commit
                    // policy stays conservative. Only committed text feeds the final
                    // transcript and text injection.
                    var provisional = _recognizer!.PartialText;
                    var provisionalTail = string.IsNullOrEmpty(provisional)
                        ? ""
                        : postProc.Process(provisional, processingSettings.CompiledRules);

                    string display;
                    if (_partialProcessedText.Length == 0)
                        display = provisionalTail;
                    else if (string.IsNullOrEmpty(provisionalTail))
                        display = _partialProcessedText.ToString();
                    else
                        display = _partialProcessedText + " " + provisionalTail;

                    if (!string.IsNullOrEmpty(display))
                        PartialResult?.Invoke(display);

                    if (raw is not null && _partialProcessedText.Length > 0)
                        CommittedResult?.Invoke(_partialProcessedText.ToString());
                }
                gotData = true;
            }

            if (!gotData)
            {
                _signal?.Wait(50);
                _signal?.Reset();
            }
        }

        // The capture queue drops the oldest batches when recognition falls behind; make
        // that visible instead of letting words silently disappear from the transcript.
        var dropped = _buffer?.DroppedBatches ?? 0;
        if (dropped > 0)
            _telemetry?.LogWarning("Recognition", $"Audio capture dropped {dropped} batches — recognition could not keep up with real time");

        // If the capture source failed (missing mic, broken loopback, ...) there is no
        // audio to flush — surface the error and stop cleanly instead of firing an
        // empty FinalResult that would wipe the previously displayed text.
        if (_captureError is not null)
        {
            var message = _captureError.Message;
            _telemetry?.LogError("Recognition", $"Audio capture failed: {message}");
            CaptureError?.Invoke(message);
            Stopped?.Invoke();
            _captureThread?.Join(TimeSpan.FromSeconds(1));
            return;
        }

        // Flush
        if (_recognizer is IUtteranceStreamingRecognizer utterance)
        {
            StreamingResult result;
            lock (_recognizerOperationGate)
                result = utterance.FlushUtterance();
            HandleStreamingResult(postProc, result);
        }
        else
        {
            string? final;
            lock (_recognizerOperationGate)
                final = _recognizer!.Flush();
            if (final is not null) _accumulatedText.Append(final);
        }

        var finalProcessingSettings = Volatile.Read(ref _processingSettings);
        var finalProcessed = postProc.ProcessFinal(_accumulatedText.ToString(), finalProcessingSettings.CompiledRules);

        FinalResult?.Invoke(finalProcessed);
        Stopped?.Invoke();

        _captureThread?.Join(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Handles a streaming step from an utterance-segmenting recognizer:
    /// commits finalized text and surfaces the running partial.
    /// </summary>
    private void HandleStreamingResult(IPostProcessingPipeline postProc, StreamingResult result)
    {
        var settings = Volatile.Read(ref _processingSettings);

        if (!string.IsNullOrEmpty(result.Final))
        {
            AppendUtterance(_accumulatedText, result.Final);
            var processed = postProc.Process(result.Final, settings.CompiledRules);
            if (!string.IsNullOrEmpty(processed))
                UtteranceFinalized?.Invoke(processed);
        }

        var fullPartial = _accumulatedText.Length > 0 && result.Partial.Length > 0
            ? _accumulatedText.ToString() + " " + result.Partial
            : _accumulatedText.Length > 0
                ? _accumulatedText.ToString()
                : result.Partial;

        var processedPartial = postProc.Process(fullPartial, settings.CompiledRules);
        if (!string.IsNullOrEmpty(processedPartial))
            PartialResult?.Invoke(processedPartial);

        // Committed text only: finalized utterances plus the stable (non-preview) part
        // of the current one. Recognizers that do not report Stable fall back to the
        // finalized utterances.
        var stable = result.Stable ?? "";
        var committed = _accumulatedText.Length > 0 && stable.Length > 0
            ? _accumulatedText + " " + stable
            : _accumulatedText.Length > 0 ? _accumulatedText.ToString() : stable;
        if (committed.Length > 0)
        {
            var processedCommitted = postProc.Process(committed, settings.CompiledRules);
            if (!string.IsNullOrEmpty(processedCommitted))
                CommittedResult?.Invoke(processedCommitted);
        }
    }

    private static void AppendUtterance(StringBuilder target, string utterance)
    {
        if (string.IsNullOrEmpty(utterance)) return;
        if (target.Length > 0) target.Append(' ');
        target.Append(utterance);
    }

    public string? SaveAudio(string fileNameBase)
    {
        if (_audioRecorder is null) return null;
        var sessionMgr = _sessionManager ?? new SessionManager(telemetry: _telemetry);
        var dir = sessionMgr.EnsureDirectory();
        var path = Path.Combine(dir, fileNameBase);
        return _audioRecorder.StopAndSave(path);
    }

    /// <summary>
    /// Change the recognition language at runtime without reloading the model.
    /// Only works for multilingual models.
    /// </summary>
    public void SetLanguage(string language)
    {
        lock (_recognizerOperationGate)
        {
            if (_recognizer is null) return;

            if (_recognizer is ILanguageConfigurable languageConfigurable &&
                languageConfigurable.TrySetLanguage(language))
            {
                _telemetry?.LogInfo("Recognition", $"Language set to {language}");
            }
            else if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                _telemetry?.LogInfo("Recognition", "Selected recognizer does not support explicit language selection");
            }
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _disposed = true;
        CleanupPreviousSession();
        UnloadModel();
    }

    private bool _disposed;

    /// <summary>
    /// Dispose all per-session resources (recorder, audio source, sync primitives)
    /// to prevent memory/file-handle leaks across recognition restarts.
    /// </summary>
    private void CleanupPreviousSession()
    {
        if (_captureState is not null)
            _captureState.Stop();
        _signal?.Set();

        // Wait for the previous ProcessLoop task to fully complete
        // before disposing its resources — prevents use-after-dispose.
        if (_processTask is not null)
        {
            try { _processTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _processTask = null;
        }

        _captureThread?.Join(TimeSpan.FromSeconds(1));
        _captureThread = null;

        _audioRecorder?.Dispose();
        _audioRecorder = null;

        _audioSource?.Dispose();
        _audioSource = null;

        _signal?.Dispose();
        _signal = null;

        _buffer = null;
        _captureState?.Dispose();
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

    /// <summary>Resolve an existing model path from settings and the default model directory.</summary>
    private static string ResolveModelPath(AppSettings settings)
    {
        return ModelPathResolver.FindExistingModelPath(settings)
            ?? throw new DirectoryNotFoundException("No model directory containing genai_config.json was found.");
    }

    private sealed record RuntimeProcessingSettings(
        IReadOnlyList<PostProcessingPipeline.CompiledRule> CompiledRules)
    {
        public static RuntimeProcessingSettings Empty { get; } = new(Array.Empty<PostProcessingPipeline.CompiledRule>());
    }
}
