using SpeechLib;

namespace SpeechLib.Diarization;

/// <summary>
/// The loaded model: the front-end and step sessions plus the parsed configuration. Thread-safe and shareable
/// by many diarizers; per-stream state (mel cache, AOSC, probabilities) lives in the diarizers.
/// <see cref="Dispose"/> waits for in-flight runs; afterwards every call throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class DiarizationModel : IDisposable
{
    private readonly IDiarizationEngine _engine;
    private readonly object _gate = new();
    private int _active;
    private bool _disposed;

    internal DiarizationModel(DiarizationModelDescriptor descriptor, DiarizationConfig config,
        IDiarizationEngine engine, string executionProvider)
    {
        Descriptor = descriptor;
        Config = config;
        _engine = engine;
        ExecutionProvider = executionProvider;
    }

    /// <summary>Folder, precision and files in use.</summary>
    public DiarizationModelDescriptor Descriptor { get; }

    /// <summary>Parsed <c>diar_config.json</c>.</summary>
    public DiarizationConfig Config { get; }

    /// <summary>Execution provider actually configured ("Cpu", "Cuda", "Dml", "WebGpu").</summary>
    public string ExecutionProvider { get; }

    /// <summary>Loads and validates the model. Throws <see cref="DiarizationModelException"/> on bad files.</summary>
    public static DiarizationModel Load(DiarizationModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var descriptor = DiarizationModelLocator.Validate(options.ModelFolder, options.Precision);
        var config = DiarizationConfig.Load(descriptor.ConfigPath);

        using var stepOptions = OnnxEngine.CreateOptions(options.Threads);
        string ep = ExecutionProviderSelector.Apply(stepOptions, options.ExecutionProvider).ToString();
        options.ConfigureSessionOptions?.Invoke(stepOptions);
        // The 1.2 MB front-end is a strided Conv1d + matmul: CPU is always right for it.
        using var frontendOptions = OnnxEngine.CreateOptions(options.Threads);
        return new DiarizationModel(descriptor, config, OnnxEngine.Create(descriptor, stepOptions, frontendOptions), ep);
    }

    /// <summary>Runs the step graph. <paramref name="cached"/> holds <paramref name="cachedFrames"/> x 512 floats (may be empty).</summary>
    internal StepOutput RunStep(float[] features, int melFrames, float[] cached, int cachedFrames)
    {
        Enter();
        try { return _engine.RunStep(features, melFrames, cached, cachedFrames); }
        finally { Exit(); }
    }

    /// <summary>Runs the mel front-end on <paramref name="samples"/> (pre-emphasized, centered signal); returns frames x 128 log-mel.</summary>
    internal float[] RunFrontend(float[] samples)
    {
        Enter();
        try { return _engine.RunFrontend(samples); }
        finally { Exit(); }
    }

    private void Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active++;
        }
    }

    private void Exit()
    {
        lock (_gate)
        {
            _active--;
            if (_active == 0) Monitor.PulseAll(_gate);
        }
    }

    /// <summary>True once <see cref="Dispose"/> was called.</summary>
    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_active > 0) Monitor.Wait(_gate);
        }
        _engine.Dispose();
    }
}
