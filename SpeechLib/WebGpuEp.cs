#if GPU_WEBGPU
using Microsoft.ML.OnnxRuntime;

namespace SpeechLib;

/// <summary>
/// WebGPU plugin EP registration helper.
/// Compiles only when GpuArch=WebGPU.
/// </summary>
public static class WebGpuEp
{
    private static readonly Lock _lock = new();
    private static bool _registered;

    /// <summary>
    /// Registers the WebGPU plugin EP with ORT. Safe to call multiple times —
    /// only the first call registers; subsequent calls are no-ops.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (_lock)
        {
            if (_registered) return;

            var env = OrtEnv.Instance();
            string libPath = Microsoft.ML.OnnxRuntime.EP.WebGpu.WebGpuEp.GetLibraryPath();
            env.RegisterExecutionProviderLibrary("webgpu_ep", libPath);
            _registered = true;
        }
    }

    /// <summary>
    /// Returns the EP name used by GenAI config and GetEpDevices enumeration.
    /// </summary>
    public static string EpName => Microsoft.ML.OnnxRuntime.EP.WebGpu.WebGpuEp.GetEpName();
}
#else
namespace SpeechLib;

/// <summary>Stub — WebGPU not available in this build configuration.</summary>
public static class WebGpuEp
{
    public static void EnsureRegistered() { }
    public static string EpName => "WebGpuExecutionProvider";
}
#endif
