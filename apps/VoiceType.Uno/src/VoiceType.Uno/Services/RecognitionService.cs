using System.Text;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

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

    public RecognitionService(IAudioSourceFactory audioSourceFactory)
    {
        _audioSourceFactory = audioSourceFactory;
    }

    public event Action<string>? PartialResult;
    public event Action<string>? FinalResult;
    public event Action? Stopped;
    public event Action<ModelLifecycleState>? ModelStateChanged;

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

        _accumulatedText.Clear();
        _isRunning = true;

        _audioSource = _audioSourceFactory.Create(
            Enum.Parse<CaptureMode>(settings.AudioSource),
            _recognizer.SampleRate);

        _buffer = new ConcurrentQueueWrapper();
        _signal = new ManualResetEventSlim(false);
        _captureState = new CaptureState();

        _captureThread = new Thread(() => _audioSource.Start(_buffer, _signal, _captureState))
        {
            IsBackground = true
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

    private async Task ProcessLoop()
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

                string? raw;
                lock (_recognizerOperationGate)
                    raw = _recognizer!.ProcessAudio(batch);

                if (raw is not null)
                {
                    _accumulatedText.Append(raw);
                    PartialResult?.Invoke(_accumulatedText.ToString());
                }
            }

            if (!gotData)
            {
                _signal?.Wait(50);
                _signal?.Reset();
            }
        }

        string? final;
        lock (_recognizerOperationGate)
            final = _recognizer!.Flush();
        if (final is not null)
            _accumulatedText.Append(final);

        FinalResult?.Invoke(_accumulatedText.ToString());
        Stopped?.Invoke();

        _captureThread?.Join(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        Stop();
        UnloadModel();
        _signal?.Dispose();
    }
}

public enum ModelLifecycleState
{
    Unloaded,
    Loading,
    Loaded,
    Error
}
