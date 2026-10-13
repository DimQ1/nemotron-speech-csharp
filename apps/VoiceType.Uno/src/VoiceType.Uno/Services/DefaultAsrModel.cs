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
    public const string Title = KnownModels.DefaultTitle;

    /// <summary>Folder the model is installed into (the repo name, as before).</summary>
    public static string FolderName => RepoId[(RepoId.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Display name of an installed model folder: the default model is not part of the
    /// catalog, so <see cref="ModelCatalog.DescribeFolder"/> alone would show its raw folder name.
    /// </summary>
    public static string Describe(string? folderName) => KnownModels.Describe(folderName);

    /// <summary>Full path of the installed model folder: <c>&lt;models root&gt;/asr/&lt;folder&gt;</c>.</summary>
    public static string FolderPath(ModelStorageLayout layout) => layout.AsrModelPath(FolderName);

    /// <summary>A download request into the <c>asr/</c> folder of <paramref name="layout"/>.</summary>
    public static ModelDownloadRequest CreateRequest(ModelStorageLayout layout) =>
        new(Key, Title, RepoId, FolderPath(layout))
        {
            Kind = ModelDownloadKind.Recognition
        };
}
