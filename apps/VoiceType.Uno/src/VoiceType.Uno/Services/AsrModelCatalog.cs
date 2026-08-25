namespace VoiceType.Uno.Services;

/// <summary>
/// A single ASR model variant published on Hugging Face under DimQ1.
/// Ported from the WinUI downloader (AvailableModel.CpuModels) so the Uno app
/// can offer the same model selection when downloading.
/// </summary>
public sealed record AsrModelCatalogEntry(
    string Name,
    string RepoId,
    string Description,
    string SizeDisplay,
    string Precision,
    string? QuantizationFolder = null)
{
    /// <summary>
    /// Folder name the model is downloaded into. For single-variant repos this
    /// is the repo name; for the parakeet-tdt repo (fp32/int8/int4 subfolders)
    /// it is "{repo}-{quantization}" so each precision lands in its own folder.
    /// </summary>
    public string Subfolder => QuantizationFolder is null
        ? RepoId[(RepoId.LastIndexOf('/') + 1)..]
        : $"{RepoId[(RepoId.LastIndexOf('/') + 1)..]}-{QuantizationFolder}";

    /// <summary>ComboBox-friendly label: name + size.</summary>
    public string DisplayName => $"{Name} · {SizeDisplay}";

    public override string ToString() => DisplayName;
}

/// <summary>Predefined CPU ASR models available for download from Hugging Face.</summary>
public static class AsrModelCatalog
{
    public static IReadOnlyList<AsrModelCatalogEntry> Models { get; } = new List<AsrModelCatalogEntry>
    {
        new(
            Name: "INT4 opset24 · 0.56s — Recommended",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu",
            Description: "4-bit k-quant, opset 24, 0.56s window — low latency real-time dictation",
            SizeDisplay: "749 MB",
            Precision: "INT4"),
        new(
            Name: "INT4 opset24 · 1.12s — Best INT4 accuracy",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c112-cpu",
            Description: "4-bit k-quant, opset 24, 1.12s window — best accuracy for the quantized build",
            SizeDisplay: "749 MB",
            Precision: "INT4"),
        new(
            Name: "INT4 — Fastest",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-cpu",
            Description: "4-bit k-quant, lowest latency on CPU",
            SizeDisplay: "757 MB",
            Precision: "INT4"),
        new(
            Name: "INT8 — Balanced",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu",
            Description: "8-bit k-quant, good quality/speed balance",
            SizeDisplay: "1,021 MB",
            Precision: "INT8"),
        new(
            Name: "FP32 — Best Quality",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-cpu",
            Description: "Full precision, maximum accuracy",
            SizeDisplay: "2,479 MB",
            Precision: "FP32"),
        new(
            Name: "FP32 opset24 · 0.56s — Full precision",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c056-cpu",
            Description: "Full precision FP32, opset 24, 0.56s window",
            SizeDisplay: "2,479 MB",
            Precision: "FP32"),
        new(
            Name: "FP32 opset24 · 1.12s — Max accuracy",
            RepoId: "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c112-cpu",
            Description: "Full precision FP32, opset 24, 1.12s window — maximum accuracy",
            SizeDisplay: "2,479 MB",
            Precision: "FP32"),

        // ── Parakeet TDT 0.6B v3 (multilingual, 25 languages) ──────────
        new(
            Name: "Parakeet TDT · FP32 — Best Quality",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Description: "Multilingual TDT (25 European languages), full precision",
            SizeDisplay: "2,550 MB",
            Precision: "FP32",
            QuantizationFolder: "fp32"),
        new(
            Name: "Parakeet TDT · INT8 — Balanced",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Description: "Multilingual TDT (25 European languages), 8-bit quantization",
            SizeDisplay: "670 MB",
            Precision: "INT8",
            QuantizationFolder: "int8"),
        new(
            Name: "Parakeet TDT · INT4 — Fastest",
            RepoId: "DimQ1/parakeet-tdt-0.6b-v3-onnx",
            Description: "Multilingual TDT (25 European languages), 4-bit quantization",
            SizeDisplay: "730 MB",
            Precision: "INT4",
            QuantizationFolder: "int4"),
    };

    /// <summary>The default model downloaded when no selection is made.</summary>
    public static AsrModelCatalogEntry Recommended => Models[0];
}
