namespace SpeechLib.ModelDownload;

/// <summary>
/// The single source of truth for the downloadable ASR model catalog, shared
/// between the WinUI and Uno apps. Each entry carries a commercial name, a
/// plain-language description, and structured measured metrics (WER, speed).
/// Metrics come from build/wer-reports (Common Voice 17, 250 ru + 250 en files,
/// CPU, left_context=56 for Nemotron).
/// </summary>
public static class ModelCatalog
{
    private const string Cv17 = "Common Voice 17 (250 ru + 250 en)";

    public static IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor>
    {
        // ── Parakeet TDT 0.6B v3 (multilingual, 25 European languages, streaming) ─
        new(
            CommercialName: "Parakeet TDT 0.6B v3",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Tagline: "Recommended — best accuracy, live text",
            Description: "Most accurate model on our test set, now with live streaming: words show within a fraction of a second and settle about a second later. 25 European languages, compact 4-bit download.",
            SizeBytes: 730_850_263,
            Precision: ModelPrecision.Int4,
            ContextWindow: null,
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.Multilingual,
            Research: new ModelResearch(new WerMetrics(8.10, 6.04, 9.99), new SpeedMetrics(0.186), Cv17, "build/wer-reports/parakeet-tdt-int4-20260828.md"),
            QuantizationFolder: "int4",
            IsRecommended: true),
        new(
            CommercialName: "Parakeet TDT 0.6B v3",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Tagline: "Best accuracy, full precision",
            Description: "Full-precision Parakeet with live streaming. Slightly more accurate than INT4, at the cost of a much larger download and more memory.",
            SizeBytes: 2_549_945_719,
            Precision: ModelPrecision.Fp32,
            ContextWindow: null,
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.Multilingual,
            Research: new ModelResearch(new WerMetrics(7.96, 5.75, 9.99), new SpeedMetrics(0.190), Cv17, "build/wer-reports/parakeet-tdt-fp32-20260828.md"),
            QuantizationFolder: "fp32"),
        new(
            CommercialName: "Parakeet TDT 0.6B v3",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Tagline: "Fastest Parakeet",
            Description: "Lightest CPU load of the Parakeet variants, with lower accuracy on our test set.",
            SizeBytes: 670_619_803,
            Precision: ModelPrecision.Int8,
            ContextWindow: null,
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.Multilingual,
            Research: new ModelResearch(new WerMetrics(12.15, 9.82, 14.29), new SpeedMetrics(0.141), Cv17, "build/wer-reports/parakeet-tdt-int8-20260828.md"),
            QuantizationFolder: "int8"),

        // ── Nemotron 3.5 ASR (RNN-T, ONNX Runtime GenAI, streaming) ──
        new(
            CommercialName: "Nemotron 3.5 ASR",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c112-cpu",
            Tagline: "Higher quality, low CPU load",
            Description: "More audio context for better accuracy and the lowest CPU load. Text appears with a slight lag.",
            SizeBytes: 793_577_927,
            Precision: ModelPrecision.Int4,
            ContextWindow: "1.12s",
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.HighQuality,
            Research: new ModelResearch(new WerMetrics(19.21, 15.72, 22.41), new SpeedMetrics(0.142), Cv17, "build/wer-reports/nemotron-cpu-int4-c112-20260828.md")),
        new(
            CommercialName: "Nemotron 3.5 ASR",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-c112-cpu",
            Tagline: "Best accuracy",
            Description: "Full precision and maximum accuracy, at the cost of a larger download and more memory.",
            SizeBytes: 2_599_226_295,
            Precision: ModelPrecision.Fp32,
            ContextWindow: "1.12s",
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.HighQuality,
            Research: new ModelResearch(new WerMetrics(16.71, 12.52, 20.55), new SpeedMetrics(0.143), Cv17, "build/wer-reports/nemotron-cpu-fp32-c112-20260828.md")),
        new(
            CommercialName: "Nemotron 3.5 ASR",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu",
            Tagline: "Fast response, low CPU load",
            Description: "Most responsive — words appear almost instantly as you speak. Compact 4-bit size.",
            SizeBytes: 793_577_927,
            Precision: ModelPrecision.Int4,
            ContextWindow: "0.56s",
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.FastDictation,
            Research: new ModelResearch(new WerMetrics(20.25, 16.78, 23.44), new SpeedMetrics(0.199), Cv17, "build/wer-reports/nemotron-cpu-int4-c056-20260828.md")),
        new(
            CommercialName: "Nemotron 3.5 ASR",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-c056-cpu",
            Tagline: "Fast response, full precision",
            Description: "Full precision with the shortest delay. Good when you want responsiveness without giving up accuracy.",
            SizeBytes: 2_599_226_295,
            Precision: ModelPrecision.Fp32,
            ContextWindow: "0.56s",
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.FastDictation,
            Research: new ModelResearch(new WerMetrics(17.66, 13.83, 21.17), new SpeedMetrics(0.254), Cv17, "build/wer-reports/nemotron-cpu-fp32-c056-20260828.md")),

        // ── Qwen3-ASR 1.7B (hybrid INT4 decoder, block streaming) ──
        new(
            CommercialName: "Qwen3-ASR 1.7B",
            RepoId: "andrewleech/qwen3-asr-1.7b-onnx",
            Tagline: "Qwen3 quality · block streaming",
            Description: "High-quality multilingual recognition using Qwen3's decoder with 2-second encoder blocks and a fixed 16-second feature context. Text appears sooner, with a higher CPU load than Nemotron.",
            SizeBytes: 4_133_826_683,
            Precision: ModelPrecision.Int4,
            ContextWindow: "16s",
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.HighQuality,
            Research: new ModelResearch(Dataset: Cv17)),

    };

    /// <summary>The recommended everyday model (Parakeet TDT INT4: best accuracy, live streaming).</summary>
    public static ModelDescriptor Recommended => Models.First(m => m.IsRecommended);

    /// <summary>
    /// Catalog entries in display order: the recommended model first, then the
    /// remaining variants with their family kept together (stable sort).
    /// </summary>
    public static IReadOnlyList<ModelDescriptor> OrderedModels { get; } =
        Models.OrderBy(m => m.IsRecommended ? 0 : 1).ToList();

    /// <summary>Star ratings and category winners, computed once for the whole catalog.</summary>
    public static ModelRankingTable Ranking { get; } = ModelRankingTable.For(Models);

    /// <summary>
    /// Human-readable name of a downloaded model folder: the catalog title when the
    /// folder belongs to a known variant, otherwise the folder name itself.
    /// </summary>
    public static string DescribeFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return "no model selected";

        return FindBySubfolder(folderName)?.Title ?? folderName;
    }

    /// <summary>
    /// Finds a catalog entry by its downloaded folder name (<see cref="ModelDescriptor.SubfolderName"/>),
    /// or null when the folder is not part of the catalog (custom/local model).
    /// </summary>
    public static ModelDescriptor? FindBySubfolder(string folderName)
        => Models.FirstOrDefault(m =>
            string.Equals(m.SubfolderName, folderName, StringComparison.OrdinalIgnoreCase));
}
