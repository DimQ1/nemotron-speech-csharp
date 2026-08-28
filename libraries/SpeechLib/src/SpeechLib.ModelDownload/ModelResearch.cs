namespace SpeechLib.ModelDownload;

/// <summary>Word error rate measured on a test set.</summary>
public sealed record WerMetrics(double TotalPercent, double? RuPercent = null, double? EnPercent = null);

/// <summary>
/// Processing speed relative to real time. <see cref="SpeedMultiplier"/> is derived
/// from the measured real-time factor (1 / RTF).
/// </summary>
public sealed record SpeedMetrics(double RealTimeFactor)
{
    public double SpeedMultiplier => RealTimeFactor > 0 ? 1.0 / RealTimeFactor : 0;

    public bool IsRealtimeCapable => RealTimeFactor < 1.0;
}

/// <summary>
/// Structured evaluation results for a model. A null metric means "not measured"
/// and must be rendered as "no tests" rather than an invented number.
/// </summary>
public sealed record ModelResearch(
    WerMetrics? Wer = null,
    SpeedMetrics? Speed = null,
    string? Dataset = null,
    string? Source = null)
{
    public bool HasWer => Wer is not null;

    public bool HasSpeed => Speed is not null;

    public bool HasAnyData => HasWer || HasSpeed;
}
