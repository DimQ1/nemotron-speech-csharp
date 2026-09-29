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

    /// <summary>
    /// Human-readable precision, e.g. "4-bit (INT4)" — spelled out so a list of
    /// variants of the same family (INT4/INT8/FP32) stays distinguishable.
    /// </summary>
    public string PrecisionSummary => Precision switch
    {
        ModelPrecision.Fp32 => "full precision (FP32)",
        ModelPrecision.Int8 => "8-bit (INT8)",
        ModelPrecision.Int4 => "4-bit (INT4)",
        _ => Precision.ToString()
    };

    /// <summary>Audio window the model consumes, e.g. "1.12s window" or "live streaming".</summary>
    public string WindowSummary => ContextWindow is null
        ? "live streaming"
        : $"{ContextWindow} window";

    /// <summary>What the model is optimized for, in plain words.</summary>
    public string UseCaseText => UseCase switch
    {
        ModelUseCase.FastDictation => "Fast dictation",
        ModelUseCase.HighQuality => "High accuracy",
        ModelUseCase.Multilingual => "Multilingual",
        _ => UseCase.ToString()
    };

    /// <summary>
    /// Unambiguous card title: commercial name, spelled-out precision and — when
    /// the family ships several audio windows (Nemotron 1.12s vs 0.56s) — the window,
    /// so no two catalog entries share a title.
    /// </summary>
    public string Title => ContextWindow is null
        ? $"{CommercialName} — {PrecisionSummary}"
        : $"{CommercialName} — {PrecisionSummary} · {ContextWindow} window";

    public string SizeText => ModelMetricsFormatter.FormatSize(SizeBytes);

    /// <summary>Compact accuracy badge, e.g. "WER 8.1%".</summary>
    public string AccuracyText => ModelMetricsFormatter.FormatWer(Research.Wer);

    /// <summary>Per-language accuracy breakdown, e.g. "ru 5.8% / en 10.0%".</summary>
    public string AccuracyDetailText => ModelMetricsFormatter.FormatWerDetail(Research.Wer);

    /// <summary>Speed badge, e.g. "≈5.4× real-time".</summary>
    public string SpeedText => ModelMetricsFormatter.FormatSpeed(Research.Speed);

    public string LatencyText => ModelMetricsFormatter.FormatLatency(Latency);

    /// <summary>One-line summary of every measurable property, used in tooltips.</summary>
    public string MetricsLine => string.Join(" · ", new[]
    {
        AccuracyText,
        SpeedText,
        SizeText,
        LatencyText,
        UseCaseText
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Legacy list label (title + download size).</summary>
    public string DisplayName => $"{Title} · {SizeText}";

    public override string ToString() => DisplayName;

    private string PrecisionDisplay => Precision switch
    {
        ModelPrecision.Fp32 => "FP32",
        ModelPrecision.Int8 => "INT8",
        ModelPrecision.Int4 => "INT4",
        _ => Precision.ToString()
    };
}
