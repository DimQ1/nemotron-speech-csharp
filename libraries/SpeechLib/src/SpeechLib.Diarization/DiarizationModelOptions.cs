using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpeechLib.Diarization;

/// <summary>How to load the diarization model.</summary>
public sealed class DiarizationModelOptions
{
    /// <summary>Folder holding <c>diar_step[.int8].onnx</c>, <c>diar_frontend.onnx</c> and <c>diar_config.json</c>.</summary>
    public string ModelFolder { get; set; } = "";

    /// <summary>
    /// <see cref="DiarizationPrecision.Auto"/> picks int8 when the folder has it. Int8 is the recommended
    /// default (4x smaller, 2-3x faster, same DER); fp32 is the reference.
    /// </summary>
    public DiarizationPrecision Precision { get; set; } = DiarizationPrecision.Auto;

    /// <summary>
    /// Intra-op threads of the step graph. 4 was the fastest on a 8P+12E laptop (more threads were slower:
    /// E-cores join in). 0 leaves ONNX Runtime's default (all cores). Use 2 on phones next to streaming ASR.
    /// </summary>
    public int Threads { get; set; } = 4;

    /// <summary>
    /// Requested execution provider, routed through <c>ExecutionProviderSelector.Apply</c>: unknown or unavailable
    /// providers (for example a stale persisted "cuda") fall back to CPU. null or "cpu" means CPU.
    /// </summary>
    public string? ExecutionProvider { get; set; }

    /// <summary>Optional last-minute tweak of the step graph's <see cref="SessionOptions"/> (profiling, arena settings).</summary>
    public Action<SessionOptions>? ConfigureSessionOptions { get; set; }
}
