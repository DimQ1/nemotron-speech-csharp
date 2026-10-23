using Microsoft.ML.OnnxRuntime;

namespace SpeechLib.Nemotron;

/// <summary>
/// The three ONNX Runtime sessions of a Nemotron model (encoder, prediction network, joint), each with the options of its own, and
/// the guarded way they are created.
/// </summary>
/// <remarks>
/// <para><b>One provider per graph.</b> The encoder gets what <see cref="NemotronProviderPolicy"/> decided (CPU, or WebGPU on a
/// discrete graphics card). The prediction network and the joint are created CPU-only whatever was requested, with the same
/// intra-op thread count as the CPU encoder (measured: one thread for these two tiny graphs is 8 percent slower per audio second
/// than the shared count, the joint's 640 x 13088 projection still profits from the threads; the text is identical either way).</para>
/// <para><b>Guarded creation.</b> The plugin appends its provider without checking anything and reports a problem (a driver that
/// cannot create the device, no D3D12 in a remote session, out of video memory) only when the session is built, from
/// <c>new InferenceSession</c>. An encoder that fails that way is created again on the CPU, with one warning, and WebGPU is closed
/// for the rest of the process (<see cref="WebGpuProcessGate.ReportFailure"/>). A failure of a CPU session propagates.</para>
/// <para><b>Crash marker.</b> A native crash cannot be caught. The marker file is written before the WebGPU session is created
/// and removed once the encoder has run <see cref="RunsUntilProven"/> times (the warm-up run counts) or the sessions are disposed
/// after a clean close, so an ended process that left it behind keeps the next start on the CPU (see <see cref="WebGpuCrashMarker"/>).</para>
/// <para>Not thread-safe, like the engine that owns it.</para>
/// </remarks>
internal sealed class NemotronSessions : IDisposable
{
    /// <summary>Encoder runs, the warm-up run included, after which WebGPU counts as working and the crash marker is removed.</summary>
    internal const int RunsUntilProven = 4;

    private readonly NemotronProviderServices _services;
    private readonly NemotronProviderPlan _plan;
    private readonly string _directory;
    private readonly NemotronModelConfig _config;
    private readonly int _encoderThreads;
    private readonly bool? _sequential;
    private readonly SessionOptions _smallGraphOptions;
    private SessionOptions _encoderOptions;
    private WebGpuProcessGate.Lease? _lease;
    private bool _armed;
    private int _runs;

    private NemotronSessions(string modelDirectory, NemotronModelConfig config, NemotronEngineOptions options, NemotronProviderServices services, NemotronProviderPlan plan)
    {
        _directory = modelDirectory;
        _config = config;
        _services = services;
        _plan = plan;
        _lease = plan.Lease;
        _sequential = options.SequentialExecution;
        _encoderThreads = options.CpuThreads ?? NemotronRnntEngine.ComputeOptimalIntraThreads(Environment.ProcessorCount);
        Provider = plan.Info;
        _smallGraphOptions = CreateOptions(_encoderThreads);
        _encoderOptions = CreateOptions(_encoderThreads);
    }

    /// <summary>The encoder graph.</summary>
    public InferenceSession Encoder { get; private set; } = null!;

    /// <summary>The prediction network (always on the CPU).</summary>
    public InferenceSession Decoder { get; private set; } = null!;

    /// <summary>The joint network (always on the CPU).</summary>
    public InferenceSession Joint { get; private set; } = null!;

    /// <summary>What was asked for and what the encoder runs on now (it changes when a failed WebGPU encoder is replaced).</summary>
    public NemotronProviderInfo Provider { get; private set; }

    /// <summary>Creates the three sessions of a model folder.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="NemotronEngineOptions.CpuThreads"/> is not positive.</exception>
    public static NemotronSessions Create(string modelDirectory, NemotronModelConfig config, NemotronEngineOptions options)
    {
        if (options.CpuThreads is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.CpuThreads, "CPU thread count must be positive.");

        var services = options.Services ?? NemotronProviderServices.Default;
        var plan = NemotronProviderPolicy.Decide(options.ExecutionProvider, services);
        NemotronSessions? sessions = null;
        try
        {
            sessions = new NemotronSessions(modelDirectory, config, options, services, plan);
            sessions.Open();
            return sessions;
        }
        catch
        {
            if (sessions is not null)
                sessions.Dispose();
            else
                plan.Lease?.Dispose();
            throw;
        }
    }

    /// <summary>Called after every successful encoder run: the crash marker goes once WebGPU has proven itself.</summary>
    public void EncoderRan()
    {
        if (_armed && ++_runs >= RunsUntilProven)
            Disarm();
    }

    /// <summary>
    /// Replaces an encoder whose run failed on WebGPU by one on the CPU. WebGPU stays closed for the rest of the process.
    /// If the CPU session cannot be created the exception propagates and nothing has changed.
    /// </summary>
    public void ReplaceEncoderWithCpu(string reason)
    {
        var failedOptions = _encoderOptions;
        var failed = Encoder;
        _encoderOptions = CreateOptions(_encoderThreads);
        try
        {
            Encoder = _services.CreateSession(Path.Combine(_directory, _config.Encoder.FileName), _encoderOptions, false);
        }
        catch
        {
            _encoderOptions.Dispose();
            _encoderOptions = failedOptions;
            throw;
        }

        failed.Dispose();
        failedOptions.Dispose();
        GiveUpWebGpu(reason);
    }

    private void Open()
    {
        OpenEncoder();
        Decoder = _services.CreateSession(Path.Combine(_directory, _config.Decoder.FileName), _smallGraphOptions, false);
        Joint = _services.CreateSession(Path.Combine(_directory, _config.Joiner.FileName), _smallGraphOptions, false);
    }

    private void OpenEncoder()
    {
        var path = Path.Combine(_directory, _config.Encoder.FileName);
        if (_plan.Device is { } device)
        {
            string? error;
            if (_services.Adapters.TryApplyHighPerformance(_encoderOptions, device, out error))
            {
                _services.Marker.Arm();
                _armed = true;
                try
                {
                    Encoder = _services.CreateSession(path, _encoderOptions, true);
                    _services.Log($"  Nemotron: encoder on WebGPU ({Provider.Adapter}), prediction network and joint on CPU.");
                    if (Provider.Reason is { } ignored)
                        _services.Log($"  Warning: WebGPU request {ignored}.");
                    return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    error = $"the session could not be created: {e.Message}";
                }
            }

            _encoderOptions.Dispose();
            _encoderOptions = CreateOptions(_encoderThreads);
            GiveUpWebGpu(error!);
        }

        Encoder = _services.CreateSession(path, _encoderOptions, false);
    }

    private void GiveUpWebGpu(string reason)
    {
        _services.Log($"  Warning: WebGPU failed for the Nemotron encoder ({reason}); using CPU.");
        _services.Gate.ReportFailure(reason);
        Disarm();
        _lease?.Dispose();
        _lease = null;
        Provider = NemotronProviderInfo.Cpu(Provider.Requested, reason);
    }

    private void Disarm()
    {
        if (!_armed)
            return;
        _armed = false;
        _services.Marker.Disarm();
    }

    private SessionOptions CreateOptions(int threads)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        if (_sequential is { } sequential)
            options.ExecutionMode = sequential ? ExecutionMode.ORT_SEQUENTIAL : ExecutionMode.ORT_PARALLEL;
        OrtCpuTuning.DisableThreadSpinning(options);
        return options;
    }

    /// <summary>Disposes the sessions, then removes the crash marker and gives the WebGPU gate back: a crash while closing a WebGPU session must leave the marker.</summary>
    public void Dispose()
    {
        Joint?.Dispose();
        Decoder?.Dispose();
        Encoder?.Dispose();
        _smallGraphOptions.Dispose();
        _encoderOptions.Dispose();
        Disarm();
        _lease?.Dispose();
        _lease = null;
    }
}
