using System.IO;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.Models;

/// <summary>
/// Static metadata for the LiteRT translation model (Gemma 4 E2B IT in
/// <c>.litertlm</c> format). The upstream Hugging Face repo contains several
/// <c>.litertlm</c> variants; we download only the single generic build.
/// </summary>
public static class TranslationModelInfo
{
    /// <summary>Hugging Face repo that hosts the LiteRT-LM model.</summary>
    public const string RepoId = "litert-community/gemma-4-E2B-it-litert-lm";

    /// <summary>Exact file to download from the repo.</summary>
    public const string FileName = "gemma-4-E2B-it.litertlm";

    /// <summary>Compute backend. The LiteRT C API has no CUDA support; "gpu" maps to a
    /// WebGPU delegate that is unreliable on older GPUs, so we pin CPU.</summary>
    public const string Backend = "cpu";

    /// <summary>Local destination for the downloaded model file.</summary>
    public static string LocalModelPath => Path.Combine(AppPaths.TranslationModelsDir, FileName);

    /// <summary>True when the model file already exists on disk.</summary>
    public static bool IsDownloaded => File.Exists(LocalModelPath);
}
