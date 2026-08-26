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
        // ── Nemotron 3.5 ASR (RNN-T, ONNX Runtime GenAI) ───────────────
        new(
            Name: "Nemotron 3.5 ASR · INT4 opset24 · 0.56s — Recommended",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu",
            Description: "4-bit k-quant, opset 24, 0.56s window — low latency real-time dictation",
            SizeDisplay: "749 MB",
            Precision: "INT4"),
        new(
            Name: "Nemotron 3.5 ASR · INT4 opset24 · 1.12s — Best INT4 accuracy",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c112-cpu",
            Description: "4-bit k-quant, opset 24, 1.12s window — best accuracy for the quantized build",
            SizeDisplay: "749 MB",
            Precision: "INT4"),
        new(
            Name: "Nemotron 3.5 ASR · INT4 — Fastest",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-cpu",
            Description: "4-bit k-quant, lowest latency on CPU",
            SizeDisplay: "757 MB",
            Precision: "INT4"),
        new(
            Name: "Nemotron 3.5 ASR · INT8 — Balanced",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu",
            Description: "8-bit k-quant, good quality/speed balance",
            SizeDisplay: "1,021 MB",
            Precision: "INT8"),
        new(
            Name: "Nemotron 3.5 ASR · FP32 — Best Quality",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-cpu",
            Description: "Full precision, maximum accuracy",
            SizeDisplay: "2,479 MB",
            Precision: "FP32"),
        new(
            Name: "Nemotron 3.5 ASR · FP32 opset24 · 0.56s — Full precision",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c056-cpu",
            Description: "Full precision FP32, opset 24, 0.56s window",
            SizeDisplay: "2,479 MB",
            Precision: "FP32"),
        new(
            Name: "Nemotron 3.5 ASR · FP32 opset24 · 1.12s — Max accuracy",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c112-cpu",
            Description: "Full precision FP32, opset 24, 1.12s window — maximum accuracy",
            SizeDisplay: "2,479 MB",
            Precision: "FP32"),

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

    /// <summary>The default model downloaded when no selection is made.</summary>
    public static ModelDescriptor Recommended => Models[0];
}
