using Microsoft.ML.OnnxRuntime;

namespace SpeechLib;

/// <summary>
/// Resolves the ONNX Runtime execution provider to configure for a session,
/// falling back to CPU when the requested one is not available at runtime.
/// </summary>
/// <remarks>
/// GPU providers are optional: they only exist when the matching native
/// provider library ships next to the app. Appending a provider that is not
/// present throws and would fail session creation outright, so a persisted
/// setting pointing at a provider that is no longer shipped (for example
/// "cuda" left over from a GPU build) must degrade to CPU instead of breaking
/// recognition.
/// </remarks>
internal static class ExecutionProviderSelector
{
    /// <summary>Execution provider configured on a session.</summary>
    internal enum ProviderKind
    {
        Cpu,
        Cuda,
        Dml,
    }

    /// <summary>
    /// Maps a requested provider name to the provider that is actually
    /// available, falling back to CPU when the requested one is not among
    /// <paramref name="available"/>.
    /// </summary>
    internal static ProviderKind Select(string? requested, IReadOnlyCollection<string> available)
    {
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
