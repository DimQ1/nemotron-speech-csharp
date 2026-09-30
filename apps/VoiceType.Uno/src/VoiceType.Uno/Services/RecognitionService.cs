using System.Text;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using SpeechLib.PostProcessing;
using SpeechLib.Providers;

namespace VoiceType.Uno.Services;

/// <summary>
/// Cross-platform recognition pipeline: owns the capture loop and wires an
/// <see cref="IAudioSource"/> (platform capture) into an
/// <see cref="IStreamingSpeechRecognizer"/> (SpeechLib model session).
/// Mirrors VoiceType.WinUI RecognitionService behavior minus Win32 dependencies:
/// model lifecycle is separated from capture lifecycle; the model stays loaded
/// across Start/Stop cycles.
/// </summary>
public sealed class RecognitionService : IDisposable, IAsyncDisposable
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
    private readonly object _recognizerOperationGate = new();
    private string? _loadedModelPath;
    private string? _lastPartial;
    private Exception? _captureException;
    private IRecognitionPipeline? _pipeline;

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
    public string AccumulatedText => _pipeline?.CommittedText ?? "";
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
            var previous = Interlocked.Exchange(ref _recognizer, recognizer);
            // The pipeline owns the accumulated transcript and the per-batch rules, so it
            // is rebuilt together with the recognizer it drives.
            Volatile.Write(ref _pipeline, RecognitionPipelineFactory.Create(recognizer));
            lock (_recognizerOperationGate)
                previous?.Dispose();

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

    private static IStreamingSpeechRecognizer CreateRecognizer(AppSettings settings) =>
        RecognizerFactory.Create(new RecognizerFactoryOptions
        {
            ModelPath = settings.ModelPath,
            ExecutionProvider = settings.ExecutionProvider,
            Language = settings.Language,
            UseVad = settings.UseVad,
            RepetitionPenalty = settings.RepetitionPenalty,
        });

    public void UnloadModel()
    {
        if (_isRunning)
            Stop();

        var old = Interlocked.Exchange(ref _recognizer, null);
        Volatile.Write(ref _pipeline, null);
        lock (_recognizerOperationGate)
            old?.Dispose();

        Volatile.Write(ref _loadedModelPath, null);
        ModelState = ModelLifecycleState.Unloaded;
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
        if (_recognizer is null || _pipeline is null || ModelState != ModelLifecycleState.Loaded)
            throw new InvalidOperationException("Model is not loaded. Call LoadModelAsync first.");

        if (_processTask is not null || _audioSource is not null)
            await StopAndCleanupAsync();

        var pipeline = _pipeline;
        ApplyRuntimeSettings(settings);
        pipeline.Reset();
        _lastPartial = null;
        _captureException = null;
        _isRunning = true;

        _audioSource = _audioSourceFactory.Create(
            Enum.Parse<CaptureMode>(settings.AudioSource),
            _recognizer.SampleRate);

        _buffer = new ConcurrentQueueWrapper();
        _signal = new ManualResetEventSlim(false);
        _captureState = new CaptureState();

        // Clear buffered audio/decoder state from a previous session so the new
        // stream starts fresh (Parakeet TDT buffers audio between chunks).
        lock (_recognizerOperationGate)
            _recognizer.ResetStreamingState();

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

        _processTask = Task.Run(() => ProcessLoop(pipeline));
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
                runtimeConfigurable.TrySetSearchOptions(1, settings.RepetitionPenalty);
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

        if (!string.IsNullOrWhiteSpace(language))
            languageConfigurable.TrySetLanguage(language);
    }

    private Task ProcessLoop(IRecognitionPipeline pipeline)
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

                    RecognitionStep step;
                    lock (_recognizerOperationGate)
                        step = pipeline.Process(batch);
                    ApplyStep(step);
                }

                if (!gotData)
                {
                    _signal?.Wait(50);
                    _signal?.Reset();
                }
            }

            RecognitionStep tail;
            lock (_recognizerOperationGate)
                tail = pipeline.Flush();
            ApplyStep(tail);

            // Final pass: strip language tags AND normalize whitespace for clean output.
            FinalResult?.Invoke(FinalPostProcessing.Execute(pipeline.CommittedText));
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
    /// One decoded batch: the text to show as the live partial, and the text (when any)
    /// this batch committed to the transcript.
    /// </summary>
    private readonly record struct RecognitionStep(string? Display, string? Committed, bool IsUtteranceCommit)
    {
        public static RecognitionStep Empty { get; } = new(null, null, false);
    }

    /// <summary>
    /// Unifies the two recognizer shapes behind one per-batch step. SpeechLib exposes
    /// utterance-segmented streaming (committed text plus a revisable partial) and plain
    /// streaming (a running transcript); the decode loop must not care which one it drives.
    /// </summary>
    private interface IRecognitionPipeline
    {
        /// <summary>The transcript committed so far.</summary>
        string CommittedText { get; }

        /// <summary>Drops committed text for a new session.</summary>
        void Reset();

        RecognitionStep Process(float[] batch);

        RecognitionStep Flush();
    }

    private static class RecognitionPipelineFactory
    {
        public static IRecognitionPipeline Create(IStreamingSpeechRecognizer recognizer) =>
            recognizer is IUtteranceStreamingRecognizer utterance
                ? new UtterancePipeline(utterance)
                : new PlainPipeline(recognizer);
    }

    /// <summary>
    /// Utterance-segmented recognizer (Parakeet TDT): finalized utterances are committed
    /// with a separating space, the revisable tail is shown as the partial.
    /// </summary>
    private sealed class UtterancePipeline(IUtteranceStreamingRecognizer recognizer) : IRecognitionPipeline
    {
        private readonly StringBuilder _committed = new();

        public string CommittedText => _committed.ToString();

        public void Reset() => _committed.Clear();

        public RecognitionStep Process(float[] batch) => ToStep(recognizer.ProcessUtterance(batch));

        public RecognitionStep Flush() => ToStep(recognizer.FlushUtterance());

        private RecognitionStep ToStep(StreamingResult result)
        {
            var committed = string.IsNullOrEmpty(result.Final) ? null : result.Final;
            if (committed is not null)
            {
                if (_committed.Length > 0)
                    _committed.Append(' ');
                _committed.Append(committed);
            }

            return new RecognitionStep(ComposeDisplay(_committed, result.Partial), committed, IsUtteranceCommit: committed is not null);
        }
    }

    /// <summary>
    /// Plain streaming recognizer: every step's text is committed verbatim (no separator)
    /// and the recognizer's own <c>PartialText</c> is the revisable tail.
    /// </summary>
    private sealed class PlainPipeline(IStreamingSpeechRecognizer recognizer) : IRecognitionPipeline
    {
        private readonly StringBuilder _committed = new();

        public string CommittedText => _committed.ToString();

        public void Reset() => _committed.Clear();

        public RecognitionStep Process(float[] batch)
        {
            var raw = recognizer.ProcessAudio(batch);
            if (!string.IsNullOrEmpty(raw))
                _committed.Append(raw);

            return new RecognitionStep(ComposeDisplay(_committed, recognizer.PartialText), raw, IsUtteranceCommit: false);
        }

        public RecognitionStep Flush()
        {
            var tail = recognizer.Flush();
            if (!string.IsNullOrEmpty(tail))
                _committed.Append(tail);

            return RecognitionStep.Empty;
        }
    }

    /// <summary>
    /// Surfaces one decoded step: committed text joins the transcript, the display text
    /// becomes the live partial.
    /// </summary>
    private void ApplyStep(RecognitionStep step)
    {
        if (!string.IsNullOrEmpty(step.Committed))
        {
            var finalized = PartialPostProcessing.Execute(step.Committed);
            if (step.IsUtteranceCommit && !string.IsNullOrEmpty(finalized))
                UtteranceFinalized?.Invoke(finalized);
        }

        if (!string.IsNullOrEmpty(step.Display))
            RaisePartial(PartialPostProcessing.Execute(step.Display));
    }

    /// <summary>Committed transcript followed by the recognizer's revisable tail.</summary>
    private static string ComposeDisplay(StringBuilder committed, string? partial)
    {
        if (committed.Length == 0)
            return partial ?? "";

        return string.IsNullOrEmpty(partial) ? committed.ToString() : committed + " " + partial;
    }

    /// <summary>Reports a partial only when it differs from the previous one.</summary>
    private void RaisePartial(string text)
    {
        if (string.Equals(text, _lastPartial, StringComparison.Ordinal))
            return;
        _lastPartial = text;
        PartialResult?.Invoke(text);
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

    public async ValueTask DisposeAsync()
    {
        await StopAndCleanupAsync().ConfigureAwait(false);
        UnloadModel();
    }

    /// <summary>
    /// Signals shutdown without blocking the calling thread; the teardown continues in
    /// the background. Callers that can await should use <see cref="DisposeAsync"/>, which
    /// completes deterministically.
    /// </summary>
    public void Dispose() => _ = DisposeInBackgroundAsync();

    private async Task DisposeInBackgroundAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }
}

public enum ModelLifecycleState
{
    Unloaded,
    Loading,
    Loaded,
    Error
}
