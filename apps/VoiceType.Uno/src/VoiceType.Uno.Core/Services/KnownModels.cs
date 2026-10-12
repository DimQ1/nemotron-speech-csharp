using SpeechLib.ModelDownload;

namespace VoiceType.Uno.Services;

/// <summary>
/// Display names of the speech models, including the first-run default that is not part of the
/// model catalog (it is published as a single variant at the repo root).
/// </summary>
public static class KnownModels
{
    /// <summary>Folder the first-run default model is installed into (its Hugging Face repo name).</summary>
    public const string DefaultFolder = "nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu";

    /// <summary>Catalog-style title of the first-run default model.</summary>
    public const string DefaultTitle = "Nemotron 3.5 ASR · INT4 · 1.12s window";

    /// <summary>
    /// Human title of an installed model folder: the default model's title, a catalog title, or the
    /// folder name for a custom model. "no model selected" for an empty folder.
    /// </summary>
    public static string Describe(string? folderName) =>
        string.Equals(folderName, DefaultFolder, StringComparison.OrdinalIgnoreCase)
            ? DefaultTitle
            : ModelCatalog.DescribeFolder(folderName);
}
