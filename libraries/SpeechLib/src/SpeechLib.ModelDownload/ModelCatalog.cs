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
            Tagline: "Recommended — fast response",
            Description: "Most responsive — words appear almost instantly as you speak. Compact 4-bit size.",
            SizeBytes: 793_577_927,
            Precision: ModelPrecision.Int4,
            ContextWindow: "0.56s",
            Latency: ModelLatencyProfile.Streaming,
            UseCase: ModelUseCase.FastDictation,
            Research: new ModelResearch(new WerMetrics(20.25, 16.78, 23.44), new SpeedMetrics(0.199), Cv17, "build/wer-reports/nemotron-cpu-int4-c056-20260828.md"),
            IsRecommended: true),
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

        // ── Parakeet TDT 0.6B v3 (multilingual, 25 European languages) ─
        new(
            CommercialName: "Parakeet TDT 0.6B v3",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Tagline: "Multilingual · highest accuracy",
            Description: "NVIDIA's multilingual model for 25 European languages. Most accurate on our test set, but heavier on the CPU — text appears after each phrase.",
            SizeBytes: 2_549_945_719,
            Precision: ModelPrecision.Fp32,
            ContextWindow: null,
            Latency: ModelLatencyProfile.Delayed,
            UseCase: ModelUseCase.Multilingual,
            Research: new ModelResearch(new WerMetrics(7.96, 5.75, 9.99), new SpeedMetrics(0.190), Cv17, "build/wer-reports/parakeet-tdt-fp32-20260828.md"),
            QuantizationFolder: "fp32"),
        new(
            CommercialName: "Parakeet TDT 0.6B v3",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Tagline: "Multilingual · compact & accurate",
            Description: "Compact 4-bit multilingual model — near-full quality in a small download, with higher CPU load than Nemotron.",
            SizeBytes: 730_850_263,
            Precision: ModelPrecision.Int4,
            ContextWindow: null,
            Latency: ModelLatencyProfile.Delayed,
            UseCase: ModelUseCase.Multilingual,
            Research: new ModelResearch(new WerMetrics(8.10, 6.04, 9.99), new SpeedMetrics(0.186), Cv17, "build/wer-reports/parakeet-tdt-int4-20260828.md"),
            QuantizationFolder: "int4"),
        new(
            CommercialName: "Parakeet TDT 0.6B v3",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Tagline: "Multilingual · fastest",
            Description: "Fastest multilingual option, with lower accuracy on our test set.",
            SizeBytes: 670_619_803,
            Precision: ModelPrecision.Int8,
            ContextWindow: null,
            Latency: ModelLatencyProfile.Delayed,
            UseCase: ModelUseCase.Multilingual,
            Research: new ModelResearch(new WerMetrics(12.15, 9.82, 14.29), new SpeedMetrics(0.141), Cv17, "build/wer-reports/parakeet-tdt-int8-20260828.md"),
            QuantizationFolder: "int8"),
    };

    /// <summary>The recommended everyday model (fast response, low CPU load).</summary>
    public static ModelDescriptor Recommended => Models.First(m => m.IsRecommended);

    /// <summary>
    /// Finds a catalog entry by its downloaded folder name (<see cref="ModelDescriptor.SubfolderName"/>),
    /// or null when the folder is not part of the catalog (custom/local model).
    /// </summary>
    public static ModelDescriptor? FindBySubfolder(string folderName)
        => Models.FirstOrDefault(m =>
            string.Equals(m.SubfolderName, folderName, StringComparison.OrdinalIgnoreCase));
}
