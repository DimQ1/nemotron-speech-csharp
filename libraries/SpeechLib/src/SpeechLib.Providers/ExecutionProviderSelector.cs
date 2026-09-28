using Microsoft.ML.OnnxRuntime;

namespace SpeechLib;

/// <summary>
/// Resolves the ONNX Runtime execution provider to configure for a session,
/// falling back to CPU when the requested one is not available at runtime.
/// </summary>
/// <remarks>
/// <para>
/// GPU providers are optional: they only exist when the matching native
/// provider library ships next to the app. Appending a provider that is not
/// present throws and would fail session creation outright, so a persisted
/// setting pointing at a provider that is no longer shipped (for example
/// "cuda" left over from a GPU build) must degrade to CPU instead of breaking
/// recognition.
/// </para>
/// <para>
/// <c>webgpu</c> requests are handled by <see cref="WebGpuExecutionProvider"/>,
/// which registers the plugin EP at runtime. The request may carry an adapter
/// selector and provider options, for example <c>webgpu:1</c>,
/// <c>webgpu:hp</c> or <c>webgpu:0,layout=NHWC,capture=1</c>.
/// </para>
/// </remarks>
internal static class ExecutionProviderSelector
{
    /// <summary>Execution provider configured on a session.</summary>
    internal enum ProviderKind
    {
        Cpu,
        Cuda,
        Dml,
        WebGpu,
    }

    /// <summary>
    /// Maps a requested provider name to the provider that is actually
    /// available, falling back to CPU when the requested one is not among
    /// <paramref name="available"/> (or, for WebGPU, when the plugin or its
    /// adapter is missing).
    /// </summary>
    internal static ProviderKind Select(string? requested, IReadOnlyCollection<string> available)
    {
        if (WebGpuRequest.IsWebGpuRequest(requested))
            return WebGpuExecutionProvider.IsAvailable ? ProviderKind.WebGpu : ProviderKind.Cpu;

        var set = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        return requested?.Trim().ToLowerInvariant() switch
        {
            "cuda" when set.Contains("CUDAExecutionProvider") => ProviderKind.Cuda,
            "dml" when set.Contains("DmlExecutionProvider") => ProviderKind.Dml,
            _ => ProviderKind.Cpu,
        };
    }

    /// <summary>
    /// Appends the resolved provider to <paramref name="options"/>, using the
    /// providers reported by the current ONNX Runtime environment.
    /// </summary>
    /// <returns>The provider that was actually configured.</returns>
    internal static ProviderKind Apply(SessionOptions options, string? requested)
    {
        if (WebGpuRequest.TryParse(requested) is { } webGpu)
        {
            if (WebGpuExecutionProvider.TryApply(options, webGpu, out var webGpuError))
                return ProviderKind.WebGpu;

            Console.WriteLine($"  Warning: WebGPU not applied ({webGpuError}); using CPU.");
            return ProviderKind.Cpu;
        }

        var kind = Select(requested, OrtEnv.Instance().GetAvailableProviders());
        switch (kind)
        {
            case ProviderKind.Cuda:
                options.AppendExecutionProvider_CUDA(0);
                break;
            case ProviderKind.Dml:
                options.AppendExecutionProvider_DML(0);
                break;
        }

        return kind;
    }
}
