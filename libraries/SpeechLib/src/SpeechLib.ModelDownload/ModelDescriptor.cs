namespace SpeechLib.ModelDownload;

/// <summary>
/// A single downloadable ASR model variant published on Hugging Face.
/// Shared by the WinUI and Uno downloaders so both apps present the same
/// model catalog.
/// </summary>
public sealed record ModelDescriptor(
    string Name,
    string RepoId,
    string Description,
    string SizeDisplay,
    string Precision,
    string? QuantizationFolder = null,
    bool IsRecommended = false,
    double? WerPercent = null)
{
    /// <summary>
    /// Folder name the model is downloaded into. For single-variant repos this
    /// is the repo name; for the parakeet-tdt repo (fp32/int8/int4 subfolders)
    /// it is "{repo}-{quantization}" so each precision lands in its own folder.
    /// </summary>
    public string SubfolderName => QuantizationFolder is null
        ? RepoId[(RepoId.LastIndexOf('/') + 1)..]
        : $"{RepoId[(RepoId.LastIndexOf('/') + 1)..]}-{QuantizationFolder}";

    /// <summary>ComboBox-friendly label: name + size.</summary>
    public string DisplayName => $"{Name} · {SizeDisplay}";

    public override string ToString() => DisplayName;
}
