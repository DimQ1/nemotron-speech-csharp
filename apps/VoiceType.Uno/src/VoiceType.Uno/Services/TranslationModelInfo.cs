namespace VoiceType.Uno.Services;

/// <summary>
/// Static metadata for the LiteRT translation model (Gemma 4 E2B IT in
/// <c>.litertlm</c> format), used by the in-process native translation backend.
/// Mirrors VoiceType.WinUI TranslationModelInfo but resolves the path through
/// the cross-platform <see cref="AppPaths"/> (XDG data dir on Linux).
/// </summary>
public static class TranslationModelInfo
{
    /// <summary>Hugging Face repo that hosts the LiteRT-LM model.</summary>
    public const string RepoId = "litert-community/gemma-4-E2B-it-litert-lm";

    /// <summary>Exact file to download from the repo.</summary>
    public const string FileName = "gemma-4-E2B-it.litertlm";

    /// <summary>
    /// Compute backend. The LiteRT C API has no CUDA support; on Linux "gpu" maps
    /// to the WebGPU delegate (Dawn over Vulkan), which needs a Vulkan driver and
    /// is less reliable than CPU, so we pin CPU.
    /// </summary>
    public const string Backend = "cpu";

    /// <summary>Local destination for the downloaded model file.</summary>
    public static string LocalModelPath => Path.Combine(AppPaths.ModelsDir, FileName);

    /// <summary>True when the model file already exists on disk.</summary>
    public static bool IsDownloaded => File.Exists(LocalModelPath);
}
