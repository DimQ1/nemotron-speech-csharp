namespace SpeechLib.ModelDownload;

/// <summary>Quantization precision of a model export.</summary>
public enum ModelPrecision
{
    Fp32,
    Int8,
    Int4
}

/// <summary>How the recognizer delivers text to the user.</summary>
public enum ModelLatencyProfile
{
    /// <summary>Partial results stream in as speech flows (near-zero output latency).</summary>
    Streaming,

    /// <summary>Text is finalized at the end of each utterance/pause.</summary>
    Delayed
}

/// <summary>The primary use case a model is optimized for.</summary>
public enum ModelUseCase
{
    /// <summary>Fast response — type as you speak (short audio window).</summary>
    FastDictation,

    /// <summary>Higher accuracy with a slight lag (longer audio window).</summary>
    HighQuality,

    /// <summary>Multilingual recognition across many languages.</summary>
    Multilingual
}

/// <summary>
/// A single downloadable ASR model variant published on Hugging Face.
/// Shared by the WinUI and Uno downloaders so both apps present the same
/// model catalog.
/// </summary>
public sealed record ModelDescriptor(
    string CommercialName,
    string RepoId,
    string Tagline,
    string Description,
    long SizeBytes,
    ModelPrecision Precision,
    string? ContextWindow,
    ModelLatencyProfile Latency,
    ModelUseCase UseCase,
    ModelResearch Research,
    string? QuantizationFolder = null,
    bool IsRecommended = false)
{
    /// <summary>
    /// Folder name the model is downloaded into. For single-variant repos this
    /// is the repo name; for the parakeet-tdt repo (fp32/int8/int4 subfolders)
    /// it is "{repo}-{quantization}" so each precision lands in its own folder.
    /// </summary>
    public string SubfolderName => QuantizationFolder is null
        ? RepoId[(RepoId.LastIndexOf('/') + 1)..]
        : $"{RepoId[(RepoId.LastIndexOf('/') + 1)..]}-{QuantizationFolder}";

    /// <summary>Precision + context window, e.g. "INT4 · 1.12s".</summary>
    public string Variant => ContextWindow is null
        ? PrecisionDisplay
        : $"{PrecisionDisplay} · {ContextWindow}";

    /// <summary>Legacy list label (commercial name + variant + size).</summary>
    public string DisplayName => $"{CommercialName} · {Variant} · {ModelMetricsFormatter.FormatSize(SizeBytes)}";

    public override string ToString() => DisplayName;

    private string PrecisionDisplay => Precision switch
    {
        ModelPrecision.Fp32 => "FP32",
        ModelPrecision.Int8 => "INT8",
        ModelPrecision.Int4 => "INT4",
        _ => Precision.ToString()
    };
}
