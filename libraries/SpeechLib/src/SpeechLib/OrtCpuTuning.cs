using Microsoft.ML.OnnxRuntime;

namespace SpeechLib;

/// <summary>
/// Shared ONNX Runtime CPU session tuning for the ASR pipeline.
/// </summary>
public static class OrtCpuTuning
{
    /// <summary>
    /// Stops ONNX Runtime's workers from spin-waiting between kernels.
    /// </summary>
    /// <remarks>
    /// ORT's CPU kernels busy-wait for work by default. That helps a single
    /// long-running session, but this pipeline keeps several sessions alive at
    /// once (encoder, decoder init, decoder step, Silero VAD, and ORT GenAI for
    /// Nemotron) and they compete with each other and with the host-side mel pass.
    /// Measured on Qwen3-ASR block streaming (20 logical cores, 5 CV17 files):
    /// raising decoder_init from 10 to 20 threads while spinning made *every*
    /// phase ~50% slower, including the host mel pass that owns no session at all
    /// (decoderInit 774 -> 1210 ms, mel 38 -> 55 ms). Letting the workers park cut
    /// total RTF from 0.85 to 0.67 at the same thread count, and 0.60 with the
    /// full core count on the compute-bound prefill.
    /// </remarks>
    public static void DisableThreadSpinning(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
    }
}
