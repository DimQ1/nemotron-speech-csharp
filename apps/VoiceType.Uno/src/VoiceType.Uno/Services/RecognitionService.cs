using System.Text;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using SpeechLib.PostProcessing;
using SpeechLib.Providers;
using VoiceType.Uno.History;

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
    private string? _lastCommitted;
    private Exception? _captureException;
    private IRecognitionPipeline? _pipeline;
    private LiveRecognizerInfo? _recognizerInfo;

    public RecognitionService(IAudioSourceFactory audioSourceFactory)
    {
        _audioSourceFactory = audioSourceFactory;
    }

    public event Action<string>? PartialResult;

    /// <summary>
    /// The transcript the recognizer has settled on so far in this session (finished utterances plus the stable part
    /// of the current one, never revised), raised on the decode thread whenever it grows. This is what gets typed into
    /// other apps while recording, as in the WinUI app.
    /// </summary>
    public event Action<string>? CommittedResult;

    /// <summary>The whole session text, once, after recording has stopped.</summary>
    public event Action<string>? FinalResult;
    public event Action<string>? UtteranceFinalized;
    public event Action? Stopped;
    public event Action<ModelLifecycleState>? ModelStateChanged;
    public event Action<Exception>? Error;

    public bool IsRunning => _isRunning;
    public bool IsMuted => _captureMuted;
    public string AccumulatedText => _pipeline?.CommittedText ?? "";
    public string? LoadedModelPath => Volatile.Read(ref _loadedModelPath);

    /// <summary>Facts about the loaded recognizer for History version metadata; null when unloaded.</summary>
    public LiveRecognizerInfo? RecognizerInfo => Volatile.Read(ref _recognizerInfo);

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
            var info = await Task.Run(() => DescribeRecognizer(recognizer, settings.ModelPath)).ConfigureAwait(false);
            var previous = Interlocked.Exchange(ref _recognizer, recognizer);
            Volatile.Write(ref _recognizerInfo, info);
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
            Volatile.Write(ref _recognizerInfo, null);
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

    /// <summary>
    /// Recognizer family and the values that shape its output (step, contexts, end of
    /// utterance) — what a re-recognition needs to reproduce or explain the live text.
    /// </summary>
    private static LiveRecognizerInfo DescribeRecognizer(IStreamingSpeechRecognizer recognizer, string modelPath)
    {
        var values = new List<KeyValuePair<string, object?>>
        {
            new("chunkSamples", recognizer.ChunkSamples),
        };
        double commitLag = 0;
        if (recognizer is IUtteranceStreamingRecognizer utterance)
        {
            commitLag = utterance.StopHistoryEouSeconds;
            values.Add(new("stopHistoryEouSec", utterance.StopHistoryEouSeconds));
        }
        if (recognizer is SpeechLib.ParakeetTdt.ParakeetTdtRecognizer parakeet)
        {
            values.Add(new("chunkSec", parakeet.ChunkSeconds));
            values.Add(new("leftContextSec", parakeet.LeftContextSeconds));
            values.Add(new("rightContextSec", parakeet.RightContextSeconds));
            values.Add(new("silenceContextSec", parakeet.SilenceContextSeconds));
            values.Add(new("onsetContextSec", parakeet.OnsetContextSeconds));
        }

        return new LiveRecognizerInfo(
            modelPath,
            LiveRecordingMetadata.DetectFamily(modelPath),
            recognizer.SampleRate,
            recognizer is IUtteranceStreamingRecognizer,
            commitLag,
            values);
    }

    public void UnloadModel()
    {
        if (_isRunning)
            Stop();

        var old = Interlocked.Exchange(ref _recognizer, null);
        Volatile.Write(ref _pipeline, null);
        Volatile.Write(ref _recognizerInfo, null);
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
    /// <param name="tap">
    /// Optional per-session observer (History: audio tee + segment timing). It sees every
    /// batch on the decode thread; its failures never stop recognition.
    /// </param>
    public async Task StartAsync(AppSettings settings, IRecognitionTap? tap = null)
    {
        if (_recognizer is null || _pipeline is null || ModelState != ModelLifecycleState.Loaded)
            throw new InvalidOperationException("Model is not loaded. Call LoadModelAsync first.");

        if (_processTask is not null || _audioSource is not null)
            await StopAndCleanupAsync();

        var pipeline = _pipeline;
        ApplyRuntimeSettings(settings);
        pipeline.Reset();
        _lastPartial = null;
        _lastCommitted = null;
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

        _processTask = Task.Run(() => ProcessLoop(pipeline, tap));
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

    private Task ProcessLoop(IRecognitionPipeline pipeline, IRecognitionTap? tap)
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
                    {
                        // Muted audio reaches neither the recognizer nor the file; the
                        // tap records the gap so wall-clock times can be rebuilt.
                        InvokeTap(ref tap, t => t.OnMuted(batch.Length));
                        continue;
                    }

                    InvokeTap(ref tap, t => t.OnAudio(batch));
                    RecognitionStep step;
                    lock (_recognizerOperationGate)
                        step = pipeline.Process(batch);
                    InvokeTap(ref tap, t => t.OnStep(batch.Length, step.Tail, step.Committed, step.CommittedWords));
                    ApplyStep(step);
                    RaiseCommitted(pipeline);
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
            InvokeTap(ref tap, t => t.OnFlush(tail.Committed, tail.CommittedWords));
            ApplyStep(tail);
            RaiseCommitted(pipeline);

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

    /// <summary>Calls the tap; a failing tap is dropped for the rest of the session.</summary>
    private void InvokeTap(ref IRecognitionTap? tap, Action<IRecognitionTap> call)
    {
        if (tap is null)
            return;
        try
        {
            call(tap);
        }
        catch (Exception ex)
        {
            tap = null;
            Error?.Invoke(new InvalidOperationException($"History capture stopped: {ex.Message}", ex));
        }
    }

    private static readonly PostProcessingChain PartialPostProcessing =
        new PostProcessingChain().Add(new LanguageTagStripper());

    private static readonly PostProcessingChain FinalPostProcessing =
        new PostProcessingChain().Add(new LanguageTagStripper()).Add(new WhitespaceNormalizer());

    /// <summary>
    /// One decoded batch: the text to show as the live partial, and the text (when any)
    /// this batch committed to the transcript.
    /// </summary>
    /// <param name="Tail">The recognizer's revisable, uncommitted text after this batch.</param>
    private readonly record struct RecognitionStep(
        string? Display, string? Committed, bool IsUtteranceCommit, string? Tail = null,
        IReadOnlyList<SpeechLib.Models.WordTiming>? CommittedWords = null)
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

        /// <summary>
        /// <see cref="CommittedText"/> plus the stable part of the utterance still in progress: text that will not
        /// change any more, so it may be typed into another app before the utterance ends.
        /// </summary>
        string StableText { get; }

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
        private string _stable = "";

        public string CommittedText => _committed.ToString();

        public string StableText =>
            _stable.Length == 0 ? CommittedText
            : _committed.Length == 0 ? _stable
            : _committed + " " + _stable;

        public void Reset()
        {
            _committed.Clear();
            _stable = "";
        }

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

            // The stable part of the utterance that is still going on (Stable is null for recognizers that do not report it).
            _stable = result.Stable ?? "";

            return new RecognitionStep(ComposeDisplay(_committed, result.Partial), committed, IsUtteranceCommit: committed is not null, result.Partial,
                committed is null ? null : result.FinalWords);
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

        // Every step's text is committed verbatim, so it is all stable.
        public string StableText => CommittedText;

        public void Reset() => _committed.Clear();

        public RecognitionStep Process(float[] batch)
        {
            var raw = recognizer.ProcessAudio(batch);
            if (!string.IsNullOrEmpty(raw))
                _committed.Append(raw);

            var partial = recognizer.PartialText;
            return new RecognitionStep(ComposeDisplay(_committed, partial), raw, IsUtteranceCommit: false, partial);
        }

        public RecognitionStep Flush()
        {
            var tail = recognizer.Flush();
            if (!string.IsNullOrEmpty(tail))
                _committed.Append(tail);

            // Committed but not an utterance commit: nothing is raised for it (the final
            // result carries the whole text); the History tap still sees the tail.
            return new RecognitionStep(null, string.IsNullOrEmpty(tail) ? null : tail, IsUtteranceCommit: false);
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

    /// <summary>
    /// Reports the stable transcript (finished utterances plus the settled part of the current one) whenever it grew.
    /// Raised on every decode step, so text typed into other apps follows speech by a fraction of a second.
    /// </summary>
    private void RaiseCommitted(IRecognitionPipeline pipeline)
    {
        var text = PartialPostProcessing.Execute(pipeline.StableText);
        if (text.Length == 0 || string.Equals(text, _lastCommitted, StringComparison.Ordinal))
            return;

        _lastCommitted = text;
        CommittedResult?.Invoke(text);
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

/// <summary>
/// Per-session observer of the decode loop (History: audio tee and segment timing).
/// All calls arrive on the decode thread, in order; <see cref="RecognitionService.Stopped"/>
/// is raised after the last one.
/// </summary>
public interface IRecognitionTap
{
    /// <summary>A batch about to be recognized (recognizer sample rate, mono).</summary>
    void OnAudio(float[] batch);

    /// <summary>A batch of <paramref name="samples"/> dropped because capture is muted.</summary>
    void OnMuted(int samples);

    /// <summary>The batch was recognized: its uncommitted tail and the raw text it committed.</summary>
    void OnStep(int samples, string? tail, string? committed);

    /// <summary>The stream was flushed at the end of the session.</summary>
    void OnFlush(string? committed);

    /// <summary>
    /// <see cref="OnStep(int, string?, string?)"/> with the decoder's word timings of the committed
    /// text (recognizers that provide them, seconds on the session's sample timeline); null otherwise.
    /// </summary>
    void OnStep(int samples, string? tail, string? committed, IReadOnlyList<SpeechLib.Models.WordTiming>? committedWords) =>
        OnStep(samples, tail, committed);

    /// <summary><see cref="OnFlush(string?)"/> with the decoder's word timings of the committed text.</summary>
    void OnFlush(string? committed, IReadOnlyList<SpeechLib.Models.WordTiming>? committedWords) => OnFlush(committed);
}

public enum ModelLifecycleState
{
    Unloaded,
    Loading,
    Loaded,
    Error
}
