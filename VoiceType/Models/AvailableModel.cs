using System.Collections.Generic;

namespace VoiceType.Models;

/// <summary>Predefined model entry for the downloader catalog.</summary>
public sealed class AvailableModel
{
    public string Name { get; init; } = "";
    public string RepoId { get; init; } = "";
    public string Description { get; init; } = "";
    public string SizeDisplay { get; init; } = "";
    public string Precision { get; init; } = "";
    public bool IsDownloading { get; set; }
    public bool IsDownloaded { get; set; }
    public double Progress { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>Predefined CPU models available for download.</summary>
    public static IReadOnlyList<AvailableModel> CpuModels { get; } = new List<AvailableModel>
    {
        new()
        {
            Name = "FP32 — Best Quality",
            RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-cpu",
            Description = "Full precision, maximum accuracy",
            SizeDisplay = "2,479 MB",
            Precision = "FP32"
        },
        new()
        {
            Name = "INT8 — Balanced",
            RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu",
            Description = "8-bit k-quant, good quality/speed balance",
            SizeDisplay = "1,021 MB",
            Precision = "INT8"
        },
        new()
        {
            Name = "INT4 — Fastest",
            RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-cpu",
            Description = "4-bit k-quant, lowest latency on CPU",
            SizeDisplay = "757 MB",
            Precision = "INT4"
        },
    };
}
