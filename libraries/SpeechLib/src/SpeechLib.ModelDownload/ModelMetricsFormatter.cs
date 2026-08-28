namespace SpeechLib.ModelDownload;

/// <summary>
/// Formats model metrics for display. Centralizes size/speed/WER formatting so
/// UI layers (WinUI cards, Uno lists) share one implementation.
/// </summary>
public static class ModelMetricsFormatter
{
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F0} MB",
        >= 1_024 => $"{bytes / 1_024.0:F0} KB",
        _ => $"{bytes} B"
    };

    /// <summary>Compact WER badge, e.g. "WER 19.2%".</summary>
    public static string FormatWer(WerMetrics? wer)
        => wer is null ? "No WER test yet" : $"WER {wer.TotalPercent:F1}%";

    /// <summary>Per-language WER breakdown, e.g. "ru 15.7% / en 22.4%".</summary>
    public static string FormatWerDetail(WerMetrics? wer)
        => wer is not null && wer.RuPercent is not null && wer.EnPercent is not null
            ? $"ru {wer.RuPercent:F1}% / en {wer.EnPercent:F1}%"
            : "";

    /// <summary>Speed badge, e.g. "≈7.0× real-time".</summary>
    public static string FormatSpeed(SpeedMetrics? speed)
        => speed is null ? "No speed test yet" : $"≈{speed.SpeedMultiplier:F1}× real-time";

    public static string FormatLatency(ModelLatencyProfile latency) => latency switch
    {
        ModelLatencyProfile.Streaming => "Real-time",
        ModelLatencyProfile.Delayed => "Delayed output",
        _ => latency.ToString()
    };
}
