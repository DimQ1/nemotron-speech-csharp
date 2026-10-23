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
    private SwitchableCapture? _capture;
    private ConcurrentQueueWrapper? _buffer;
    private ManualResetEventSlim? _signal;
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
        if (recognizer is SpeechLib.Nemotron.NemotronUtteranceRecognizer nemotron)
        {
            values.Add(new("maxUtteranceSec", nemotron.MaxUtteranceSeconds));
            values.Add(new("autoGain", nemotron.Options.AutoGain));
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

        if (_processTask is not null || _capture is not null)
            await StopAndCleanupAsync();

        var pipeline = _pipeline;
        ApplyRuntimeSettings(settings);
        pipeline.Reset();
        _lastPartial = null;
        _lastCommitted = null;
        _captureException = null;

        var buffer = new ConcurrentQueueWrapper();
        var signal = new ManualResetEventSlim(false);
        var capture = new SwitchableCapture(_audioSourceFactory, _recognizer.SampleRate, buffer, signal);
        capture.Failed += OnCaptureFailed;

        // Clear buffered audio/decoder state from a previous session so the new
        // stream starts fresh (Parakeet TDT buffers audio between chunks).
        lock (_recognizerOperationGate)
            _recognizer.ResetStreamingState();

        // Running before the source starts: a source that fails at once reports through OnCaptureFailed, which clears it.
        _isRunning = true;
        try
        {
            capture.Start(Enum.Parse<CaptureMode>(settings.AudioSource));
        }
        catch
        {
            _isRunning = false;
            capture.Dispose();
            signal.Dispose();
            throw;
        }

        _buffer = buffer;
        _signal = signal;
        _capture = capture;
        _processTask = Task.Run(() => ProcessLoop(pipeline, tap, capture, buffer, signal));
    }

    /// <summary>The source capture runs on now (meaningful while <see cref="IsRunning"/>).</summary>
    public CaptureMode ActiveSource => _capture?.Mode ?? default;

    /// <summary>
    /// Changes the audio source of the running session without ending it: the old source is stopped and the new one
    /// started on the same queue, so the recognizer, the transcript and the History recording carry on (the stream
    /// has a gap of a few hundred milliseconds and a marker where the source changed). If the new source cannot be
    /// started the previous one is restored (<see cref="CaptureSwitchOutcome.Refused"/>); when nothing is recording the
    /// source simply applies at the next start (<see cref="CaptureSwitchOutcome.NotRunning"/>).
    /// </summary>
    public Task<CaptureSwitchResult> SwitchSourceAsync(CaptureMode mode)
    {
        var capture = _capture;
        return capture is null || !_isRunning
            ? Task.FromResult(new CaptureSwitchResult(CaptureSwitchOutcome.NotRunning, ActiveSource))
            : capture.SwitchToAsync(mode);
    }

    /// <summary>The current source died (device unplugged, driver error): the session cannot go on.</summary>
    private void OnCaptureFailed(Exception ex)
    {
        _captureException = ex;
        _isRunning = false;
        Error?.Invoke(ex);
    }

    public void Stop()
    {
        _isRunning = false;
        _capture?.Stop();
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

    private Task ProcessLoop(
        IRecognitionPipeline pipeline, IRecognitionTap? tap, SwitchableCapture capture, ConcurrentQueueWrapper buffer, ManualResetEventSlim signal)
    {
        try
        {
            // IsActive stays true while the source is being exchanged, so a switch never ends the session.
            while (capture.IsActive || !buffer.IsEmpty)
            {
                var gotData = false;
                while (buffer.TryDequeue(out var batch))
                {
                    gotData = true;
                    if (capture.IsSwitchMarker(batch, out var newSource))
                    {
                        // Not audio: the place in the stream where the source changed.
                        InvokeTap(ref tap, t => t.OnSourceChanged(newSource.ToString()));
                        continue;
                    }

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
                    InvokeTap(ref tap, t => t.OnStep(batch.Length, step.Tail, step.Committed, step.CommittedWords, step.CommittedContinuesPrevious));
                    ApplyStep(step);
                    RaiseCommitted(pipeline);
                }

                if (!gotData)
                {
                    signal.Wait(50);
                    signal.Reset();
                }
            }

            RecognitionStep tail;
            lock (_recognizerOperationGate)
                tail = pipeline.Flush();
            InvokeTap(ref tap, t => t.OnFlush(tail.Committed, tail.CommittedWords, tail.CommittedContinuesPrevious));
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
            capture.Join(TimeSpan.FromSeconds(1));
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
        _capture?.Dispose();
        _capture = null;

        _signal?.Dispose();
        _signal = null;

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

    /// <summary>
    /// <see cref="OnStep(int, string?, string?, IReadOnlyList{SpeechLib.Models.WordTiming}?)"/> that also says whether the committed text
    /// starts with the rest of the previous utterance's last word (<see cref="SpeechLib.StreamingResult.FinalContinuesPrevious"/>).
    /// </summary>
    void OnStep(int samples, string? tail, string? committed, IReadOnlyList<SpeechLib.Models.WordTiming>? committedWords,
        bool committedContinuesPrevious) =>
        OnStep(samples, tail, committed, committedWords);

    /// <summary><see cref="OnFlush(string?, IReadOnlyList{SpeechLib.Models.WordTiming}?)"/> with the same flag.</summary>
    void OnFlush(string? committed, IReadOnlyList<SpeechLib.Models.WordTiming>? committedWords, bool committedContinuesPrevious) =>
        OnFlush(committed, committedWords);

    /// <summary>
    /// The audio source changed while recording (<c>Mic</c>, <c>Loopback</c> or <c>Mix</c>): everything delivered after
    /// this call comes from <paramref name="source"/>. In order with <see cref="OnAudio"/>.
    /// </summary>
    void OnSourceChanged(string source) { }
}

public enum ModelLifecycleState
{
    Unloaded,
    Loading,
    Loaded,
    Error
}
