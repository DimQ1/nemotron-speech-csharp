using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoiceType.Models;

/// <summary>Predefined model entry for the downloader catalog with independent download state.</summary>
public sealed class AvailableModel : INotifyPropertyChanged
{
    private bool _isDownloading;
    private bool _isDownloaded;
    private double _progress;
    private string? _statusMessage;

    public string Name { get; init; } = "";
    public string RepoId { get; init; } = "";
    public string SubfolderName => RepoId.Contains('/') ? RepoId[(RepoId.LastIndexOf('/') + 1)..] : RepoId;
    public string Description { get; init; } = "";
    public string SizeDisplay { get; init; } = "";
    public string Precision { get; init; } = "";

    public bool IsDownloading
    {
        get => _isDownloading;
        set { _isDownloading = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanDownload)); }
    }

    public bool IsDownloaded
    {
        get => _isDownloaded;
        set { _isDownloaded = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanDownload)); }
    }

    public bool CanDownload => !IsDownloading && !IsDownloaded;

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

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

        // ── opset 24 builds (require ONNX Runtime ≥ 1.25) ─────────────
        new()
        {
            Name = "INT4 opset24 · 0.56s — Fast, low latency",
            RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu",
            Description = "4-bit k-quant, opset 24, 0.56s window — low latency real-time dictation",
            SizeDisplay = "749 MB",
            Precision = "INT4"
        },
        new()
        {
            Name = "INT4 opset24 · 1.12s — Best INT4 accuracy",
            RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c112-cpu",
            Description = "4-bit k-quant, opset 24, 1.12s window — best accuracy for quantized build",
            SizeDisplay = "749 MB",
            Precision = "INT4"
        },
        new()
        {
            Name = "FP32 opset24 · 0.56s — Full precision",
            RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c056-cpu",
            Description = "Full precision FP32, opset 24, 0.56s window",
            SizeDisplay = "2,479 MB",
            Precision = "FP32"
        },
        new()
        {
            Name = "FP32 opset24 · 1.12s — Max accuracy",
            RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c112-cpu",
            Description = "Full precision FP32, opset 24, 1.12s window — maximum accuracy",
            SizeDisplay = "2,479 MB",
            Precision = "FP32"
        },
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

