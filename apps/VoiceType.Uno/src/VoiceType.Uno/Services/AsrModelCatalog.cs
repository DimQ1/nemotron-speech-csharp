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
    string Precision)
{
    /// <summary>Folder name the model is downloaded into (derived from the repo id).</summary>
    public string Subfolder => RepoId[(RepoId.LastIndexOf('/') + 1)..];

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
    };

    /// <summary>The default model downloaded when no selection is made.</summary>
    public static AsrModelCatalogEntry Recommended => Models[0];
}
