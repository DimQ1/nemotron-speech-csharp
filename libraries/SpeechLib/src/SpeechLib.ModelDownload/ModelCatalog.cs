namespace SpeechLib.ModelDownload;

/// <summary>
/// The single source of truth for the downloadable ASR model catalog, shared
/// between the WinUI and Uno apps. Both Nemotron 3.5 ASR (RNN-T) and Parakeet
/// TDT variants are listed with transparent, human-readable names.
/// </summary>
public static class ModelCatalog
{
    public static IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor>
    {
        // ── Nemotron 3.5 ASR (RNN-T, ONNX Runtime GenAI, left_context=56) ──
        // WER measured on Common Voice 17 (250 ru + 250 en files) via WerEval.
        new(
            Name: "Nemotron 3.5 ASR · FP32 · 1.12s — Best accuracy",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-c112-cpu",
            Description: "Full precision, 1.12s window. WER 16.7% (ru 12.5% / en 20.6%)",
            SizeDisplay: "2,479 MB",
            Precision: "FP32",
            WerPercent: 16.71),
        new(
            Name: "Nemotron 3.5 ASR · INT4 · 1.12s — Recommended",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c112-cpu",
            Description: "4-bit k-quant, 1.12s window. WER 19.2% (ru 15.7% / en 22.4%) — best size/accuracy",
            SizeDisplay: "757 MB",
            Precision: "INT4",
            IsRecommended: true,
            WerPercent: 19.21),
        new(
            Name: "Nemotron 3.5 ASR · FP32 · 0.56s — Low latency",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-c056-cpu",
            Description: "Full precision, 0.56s window. WER 17.7% (ru 13.8% / en 21.2%)",
            SizeDisplay: "2,479 MB",
            Precision: "FP32",
            WerPercent: 17.66),
        new(
            Name: "Nemotron 3.5 ASR · INT4 · 0.56s — Low latency",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu",
            Description: "4-bit k-quant, 0.56s window. WER 20.3% (ru 16.8% / en 23.4%)",
            SizeDisplay: "757 MB",
            Precision: "INT4",
            WerPercent: 20.25),

        // ── Parakeet TDT 0.6B v3 (multilingual, 25 European languages) ─
        new(
            Name: "Parakeet TDT · INT4 — multilingual 25 lang, fastest",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Description: "Multilingual TDT (25 European languages), 4-bit quantization",
            SizeDisplay: "730 MB",
            Precision: "INT4",
            QuantizationFolder: "int4"),
        new(
            Name: "Parakeet TDT · INT8 — multilingual 25 lang, balanced",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Description: "Multilingual TDT (25 European languages), 8-bit quantization",
            SizeDisplay: "670 MB",
            Precision: "INT8",
            QuantizationFolder: "int8"),
        new(
            Name: "Parakeet TDT · FP32 — multilingual 25 lang, best quality",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Description: "Multilingual TDT (25 European languages), full precision",
            SizeDisplay: "2,550 MB",
            Precision: "FP32",
            QuantizationFolder: "fp32"),
    };

    /// <summary>The optimal model (best size/accuracy balance, from measured WER).</summary>
    public static ModelDescriptor Recommended => Models.First(m => m.IsRecommended);
}
