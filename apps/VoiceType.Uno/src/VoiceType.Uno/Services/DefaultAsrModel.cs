using SpeechLib.ModelDownload;

namespace VoiceType.Uno.Services;

/// <summary>
/// The model the app installs on first run / from the "model missing" banner.
/// Unlike the model-manager catalog (whose entries live in repo subfolders), this
/// repo publishes a single variant at the repo root — it is the long-standing
/// default of the Uno app and stays unchanged.
/// </summary>
internal static class DefaultAsrModel
{
    /// <summary>Job key: one download for this model at a time.</summary>
    public const string Key = "asr-default";

    /// <summary>Hugging Face repo of the default model.</summary>
    public const string RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu";

    /// <summary>Catalog-style title shown in the downloads list.</summary>
    public const string Title = "Nemotron 3.5 ASR · INT4 · 1.12s window";

    /// <summary>Folder the model is installed into (the repo name, as before).</summary>
    public static string FolderName => RepoId[(RepoId.LastIndexOf('/') + 1)..];

    /// <summary>Full path of the installed model folder.</summary>
    public static string FolderPath(string modelsRoot) => Path.Combine(modelsRoot, FolderName);

    /// <summary>A download request for <paramref name="modelsRoot"/>.</summary>
    public static ModelDownloadRequest CreateRequest(string modelsRoot) =>
        new(Key, Title, RepoId, FolderPath(modelsRoot))
        {
            Kind = ModelDownloadKind.Recognition
        };
}
