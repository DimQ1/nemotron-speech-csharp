using System.Text;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using SpeechLib.ParakeetTdt;
using SpeechLib.PostProcessing;

namespace VoiceType.Uno.Services;

/// <summary>
/// Cross-platform recognition pipeline: owns the capture loop and wires an
/// <see cref="IAudioSource"/> (platform capture) into an
/// <see cref="IStreamingSpeechRecognizer"/> (SpeechLib model session).
/// Mirrors VoiceType.WinUI RecognitionService behavior minus Win32 dependencies:
/// model lifecycle is separated from capture lifecycle; the model stays loaded
/// across Start/Stop cycles.
/// </summary>
public sealed class RecognitionService : IDisposable
{
    private readonly IAudioSourceFactory _audioSourceFactory;

    private IStreamingSpeechRecognizer? _recognizer;
    private IAudioSource? _audioSource;
    private Thread? _captureThread;
    private ConcurrentQueueWrapper? _buffer;
    private ManualResetEventSlim? _signal;
    private CaptureState? _captureState;
    private bool _isRunning;
    private volatile bool _captureMuted;
    private Task? _processTask;
    private readonly StringBuilder _accumulatedText = new();
    private readonly object _recognizerOperationGate = new();
    private string? _loadedModelPath;
    private Exception? _captureException;

    public RecognitionService(IAudioSourceFactory audioSourceFactory)
    {
        _audioSourceFactory = audioSourceFactory;
    }

    public event Action<string>? PartialResult;
    public event Action<string>? FinalResult;
    public event Action<string>? UtteranceFinalized;
    public event Action? Stopped;
    public event Action<ModelLifecycleState>? ModelStateChanged;
    public event Action<Exception>? Error;

    public bool IsRunning => _isRunning;
    public bool IsMuted => _captureMuted;
    public string AccumulatedText => _accumulatedText.ToString();
    public string? LoadedModelPath => Volatile.Read(ref _loadedModelPath);

    private ModelLifecycleState _modelState = ModelLifecycleState.Unloaded;
    public ModelLifecycleState ModelState
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
        if (ModelState is ModelLifecycleState.Loading or ModelLifecycleState.Loaded)
            return;

        ModelState = ModelLifecycleState.Loading;
        try
        {
            var recognizer = await Task.Run(() => CreateRecognizer(settings)).ConfigureAwait(false);
            var old = Interlocked.Exchange(ref _recognizer, recognizer);
            lock (_recognizerOperationGate)
                old?.Dispose();

            Volatile.Write(ref _loadedModelPath, settings.ModelPath);
            ModelState = ModelLifecycleState.Loaded;
        }
        catch
        {
            ModelState = ModelLifecycleState.Error;
            Volatile.Write(ref _loadedModelPath, null);
            throw;
        }
    }

    private static IStreamingSpeechRecognizer CreateRecognizer(AppSettings settings)
    {
        // Parakeet TDT (onnx-asr export) has a different decoder than the
        // Nemotron GenAI export — select the matching provider by model files.
        if (ParakeetTdtRecognizer.IsParakeetTdtModel(settings.ModelPath))
            return new ParakeetTdtRecognizer(settings.ModelPath);

        var langId = LanguageMapper.Resolve(settings.Language);
        var searchOptions = new GeneratorParamsArgs
        {
            num_beams = settings.NumBeams,
            do_sample = false,
            repetition_penalty = settings.RepetitionPenalty
        };

        IStreamingSpeechRecognizer recognizer =
            new ModelSession(settings.ModelPath, settings.ExecutionProvider, langId, settings.UseVad, searchOptions);
        return new SpeechLib.Decorators.MetricsRecognizerDecorator(recognizer, "ModelSession");
    }

    public void UnloadModel()
    {
        if (_isRunning)
            Stop();

        var old = Interlocked.Exchange(ref _recognizer, null);
        lock (_recognizerOperationGate)
            old?.Dispose();

        Volatile.Write(ref _loadedModelPath, null);
        ModelState = ModelLifecycleState.Unloaded;
    }

    // ── Capture lifecycle ──────────────────────────────────────

    public void Start(AppSettings settings)
    {
        if (_recognizer is null || ModelState != ModelLifecycleState.Loaded)
            throw new InvalidOperationException("Model is not loaded. Call LoadModelAsync first.");

        if (_processTask is not null || _audioSource is not null)
            StopAndCleanupAsync().GetAwaiter().GetResult();

        ApplyRuntimeSettings(settings);
        _accumulatedText.Clear();
        _captureException = null;
        _isRunning = true;

        _audioSource = _audioSourceFactory.Create(
            Enum.Parse<CaptureMode>(settings.AudioSource),
            _recognizer.SampleRate);

        _buffer = new ConcurrentQueueWrapper();
        _signal = new ManualResetEventSlim(false);
        _captureState = new CaptureState();

        var audioSource = _audioSource;
        var buffer = _buffer;
        var signal = _signal;
        var captureState = _captureState;
        _captureThread = new Thread(() =>
        {
            try
            {
                audioSource.Start(buffer, signal, captureState);
            }
            catch (Exception ex)
            {
                _captureException = ex;
                _isRunning = false;
                Error?.Invoke(ex);
            }
            finally
            {
                captureState.IsRunning = false;
                signal.Set();
            }
        })
        {
            IsBackground = true,
            Name = "VoiceType audio capture"
        };
        _captureThread.Start();

        _processTask = Task.Run(ProcessLoop);
    }

    public void Stop()
    {
        _isRunning = false;
        if (_captureState is not null)
            _captureState.IsRunning = false;
        _signal?.Set();
    }

    public void SetMuted(bool muted) => _captureMuted = muted;

    public void ApplyRuntimeSettings(AppSettings settings)
    {
        lock (_recognizerOperationGate)
        {
            if (_recognizer is IRuntimeConfigurable runtimeConfigurable)
            {
                runtimeConfigurable.TrySetVad(settings.UseVad);
                runtimeConfigurable.TrySetSearchOptions(settings.NumBeams, settings.RepetitionPenalty);
            }

            SetLanguageCore(settings.Language);
        }
    }

    public void SetLanguage(string language)
    {
        lock (_recognizerOperationGate)
            SetLanguageCore(language);
    }

    /// <summary>Stops capture, waits for final decoding, and releases session resources.</summary>
    public async Task StopAndCleanupAsync()
    {
        Stop();

        var processTask = _processTask;
        if (processTask is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await processTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Error?.Invoke(new TimeoutException("Recognition shutdown timed out."));
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
            finally
            {
                _processTask = null;
            }
        }

        CleanupCaptureResources();
    }

    private void SetLanguageCore(string language)
    {
        if (_recognizer is not ILanguageConfigurable languageConfigurable)
            return;

        var languageId = LanguageMapper.Resolve(language);
        if (languageId is not null)
            languageConfigurable.TrySetLanguage(languageId);
    }

    private Task ProcessLoop()
    {
        try
        {
            while ((_isRunning && _captureState?.IsRunning == true) ||
                   (_captureThread?.IsAlive == true) ||
                   (_buffer?.IsEmpty == false))
            {
                var gotData = false;
                while (_buffer?.TryDequeue(out var batch) == true)
                {
                    gotData = true;
                    if (_captureMuted)
                        continue;

                    if (_recognizer is IUtteranceStreamingRecognizer utteranceRecognizer)
                    {
                        StreamingResult result;
                        lock (_recognizerOperationGate)
                            result = utteranceRecognizer.ProcessUtterance(batch);
                        HandleStreamingResult(result);
                    }
                    else
                    {
                        string? raw;
                        lock (_recognizerOperationGate)
                            raw = _recognizer!.ProcessAudio(batch);

                        if (raw is not null)
                        {
                            _accumulatedText.Append(raw);
                            // Strip <ru-RU> language tags live so the transcript reads clean.
                            PartialResult?.Invoke(PartialPostProcessing.Execute(_accumulatedText.ToString()));
                        }
                    }
                }

                if (!gotData)
                {
                    _signal?.Wait(50);
                    _signal?.Reset();
                }
            }

            if (_recognizer is IUtteranceStreamingRecognizer utterance)
            {
                StreamingResult result;
                lock (_recognizerOperationGate)
                    result = utterance.FlushUtterance();
                HandleStreamingResult(result);
            }
            else
            {
                string? final;
                lock (_recognizerOperationGate)
                    final = _recognizer!.Flush();
                if (final is not null)
                    _accumulatedText.Append(final);
            }

            // Final pass: strip language tags AND normalize whitespace for clean output.
            FinalResult?.Invoke(FinalPostProcessing.Execute(_accumulatedText.ToString()));
        }
        catch (Exception ex)
        {
            Error?.Invoke(_captureException ?? ex);
        }
        finally
        {
            Stopped?.Invoke();
            _captureThread?.Join(TimeSpan.FromSeconds(1));
        }

        return Task.CompletedTask;
    }

    private static readonly PostProcessingChain PartialPostProcessing =
        new PostProcessingChain().Add(new LanguageTagStripper());

    private static readonly PostProcessingChain FinalPostProcessing =
        new PostProcessingChain().Add(new LanguageTagStripper()).Add(new WhitespaceNormalizer());

    /// <summary>
    /// Handles a streaming step from an utterance-segmenting recognizer:
    /// commits finalized text and surfaces the running partial.
    /// </summary>
    private void HandleStreamingResult(StreamingResult result)
    {
        if (!string.IsNullOrEmpty(result.Final))
        {
            AppendUtterance(_accumulatedText, result.Final);
            var processed = PartialPostProcessing.Execute(result.Final);
            if (!string.IsNullOrEmpty(processed))
                UtteranceFinalized?.Invoke(processed);
        }

        var fullPartial = _accumulatedText.Length > 0 && result.Partial.Length > 0
            ? _accumulatedText.ToString() + " " + result.Partial
            : _accumulatedText.Length > 0
                ? _accumulatedText.ToString()
                : result.Partial;

        var processedPartial = PartialPostProcessing.Execute(fullPartial);
        if (!string.IsNullOrEmpty(processedPartial))
            PartialResult?.Invoke(processedPartial);
    }

    private static void AppendUtterance(StringBuilder target, string utterance)
    {
        if (string.IsNullOrEmpty(utterance)) return;
        if (target.Length > 0) target.Append(' ');
        target.Append(utterance);
    }

    private void CleanupCaptureResources()
    {
        _captureThread?.Join(TimeSpan.FromSeconds(1));
        _captureThread = null;

        _audioSource?.Dispose();
        _audioSource = null;

        _signal?.Dispose();
        _signal = null;

        _captureState?.Dispose();
        _captureState = null;

        _buffer = null;
    }

    public void Dispose()
    {
        StopAndCleanupAsync().GetAwaiter().GetResult();
        UnloadModel();
    }
}

public enum ModelLifecycleState
{
    Unloaded,
    Loading,
    Loaded,
    Error
}
