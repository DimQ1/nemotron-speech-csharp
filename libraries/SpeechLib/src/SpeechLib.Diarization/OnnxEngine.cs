using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpeechLib.Diarization;

/// <summary>Result of one step graph run.</summary>
internal readonly record struct StepOutput(float[] ChunkEmbeds, float[] Probs10, float[] Probs80);

/// <summary>The two graphs behind a <see cref="DiarizationModel"/>; faked in unit tests.</summary>
internal interface IDiarizationEngine : IDisposable
{
    /// <summary>Pre-emphasized, centered signal to frames x 128 log-mel.</summary>
    float[] RunFrontend(float[] samples);

    /// <summary>One step: features (melFrames x 128), cached (cachedFrames x 512).</summary>
    StepOutput RunStep(float[] features, int melFrames, float[] cached, int cachedFrames);
}

/// <summary>The real engine: two ONNX Runtime sessions and one long-lived <see cref="RunOptions"/>.</summary>
internal sealed class OnnxEngine : IDiarizationEngine
{
    private readonly InferenceSession _step;
    private readonly InferenceSession _frontend;

    // One long-lived RunOptions: a `new RunOptions()` passed inline to Run() can be finalized by the GC while the
    // native call is still executing (no KeepAlive) -> use-after-free access violation after a few hundred steps.
    private readonly RunOptions _runOptions = new();

    private OnnxEngine(InferenceSession step, InferenceSession frontend)
    {
        _step = step;
        _frontend = frontend;
    }

    public static SessionOptions CreateOptions(int threads)
    {
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (threads > 0)
        {
            so.IntraOpNumThreads = threads;
            so.InterOpNumThreads = 1;
        }
        return so;
    }

    public static OnnxEngine Create(DiarizationModelDescriptor descriptor, SessionOptions stepOptions, SessionOptions frontendOptions)
    {
        InferenceSession? step = null, frontend = null;
        try
        {
            step = new InferenceSession(descriptor.StepPath, stepOptions);
            frontend = new InferenceSession(descriptor.FrontendPath, frontendOptions);
            Require(step, "features", "cache_embeds", "chunk_embeds", "probs", "frame_probs");
            Require(frontend, "audio", "log_mel");
            return new OnnxEngine(step, frontend);
        }
        catch (OnnxRuntimeException e)
        {
            step?.Dispose();
            frontend?.Dispose();
            throw new DiarizationModelException(
                $"ONNX Runtime could not load the diarization model from '{descriptor.Folder}': {e.Message}", e);
        }
        catch
        {
            step?.Dispose();
            frontend?.Dispose();
            throw;
        }
    }

    private static void Require(InferenceSession session, params string[] names)
    {
        foreach (var n in names)
        {
            bool found = session.InputMetadata.ContainsKey(n) || session.OutputMetadata.ContainsKey(n);
            if (!found)
                throw new DiarizationModelException(
                    $"The ONNX graph does not have the expected tensor '{n}'; the model files do not match this version of SpeechLib.Diarization.");
        }
    }

    public StepOutput RunStep(float[] features, int melFrames, float[] cached, int cachedFrames)
    {
        using var fIn = OrtValue.CreateTensorValueFromMemory(features, [1, melFrames, DiarizationConfig.MelBins]);
        // A zero-length managed array pins to a null pointer: allocate the empty tensor natively instead.
        using var cIn = cachedFrames > 0
            ? OrtValue.CreateTensorValueFromMemory(cached, [1, cachedFrames, DiarizationConfig.HiddenSize])
            : OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float,
                [1, 0, DiarizationConfig.HiddenSize]);
        using var outs = _step.Run(_runOptions,
            new Dictionary<string, OrtValue> { ["features"] = fIn, ["cache_embeds"] = cIn },
            ["chunk_embeds", "probs", "frame_probs"]);
        return new StepOutput(outs[0].GetTensorDataAsSpan<float>().ToArray(),
            outs[1].GetTensorDataAsSpan<float>().ToArray(), outs[2].GetTensorDataAsSpan<float>().ToArray());
    }

    public float[] RunFrontend(float[] samples)
    {
        using var x = OrtValue.CreateTensorValueFromMemory(samples, [1, samples.Length]);
        using var o = _frontend.Run(_runOptions, new Dictionary<string, OrtValue> { ["audio"] = x }, ["log_mel"]);
        return o[0].GetTensorDataAsSpan<float>().ToArray();
    }

    public void Dispose()
    {
        _step.Dispose();
        _frontend.Dispose();
        _runOptions.Dispose();
    }
}
