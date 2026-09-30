namespace VoiceType.Uno.Services;

using SpeechLib.ModelDownload;

/// <summary>
/// Static metadata for the LiteRT translation model (Gemma 4 E2B IT in
/// <c>.litertlm</c> format), used by the in-process native translation backend.
/// Mirrors VoiceType.WinUI TranslationModelInfo but resolves the path through
/// the cross-platform <see cref="AppPaths"/> (XDG data dir on Linux).
/// </summary>
public static class TranslationModelInfo
{
    /// <summary>Job key: one download for the translation model at a time.</summary>
    public const string Key = "translation-model";

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

    /// <summary>True when a stale temp file remains from an interrupted download.</summary>
    public static bool HasPartialDownload => File.Exists(LocalModelPath + ".part") || File.Exists(LocalModelPath + ".download");

    /// <summary>A download request for the single .litertlm file.</summary>
    public static ModelDownloadRequest CreateRequest() =>
        new(Key, $"Translation model · {FileName}", RepoId, AppPaths.ModelsDir)
        {
            SingleFile = FileName,
            Kind = ModelDownloadKind.Translation
        };

    /// <summary>Deletes the model file and any leftover temp file (repair a broken install).</summary>
    public static void DeleteDownloaded()
    {
        Delete(LocalModelPath);
        Delete(LocalModelPath + ".part");
        Delete(LocalModelPath + ".download");
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — the download retry reports the real error.
        }
    }
}
