using SpeechLib.ModelDownload;

namespace VoiceType.WinUI.ViewModels;

/// <summary>
/// Read-only presentation wrapper for a <see cref="ModelDescriptor"/> shown as a
/// card in the model downloader. Pre-computes badge text so the XAML stays free
/// of formatting logic.
/// </summary>
public sealed class ModelCardViewModel
{
    public ModelCardViewModel(ModelDescriptor descriptor) => Descriptor = descriptor;

    public ModelDescriptor Descriptor { get; }

    public string CommercialName => Descriptor.CommercialName;

    public string Tagline => Descriptor.Tagline;

    public string Description => Descriptor.Description;

    public bool IsRecommended => Descriptor.IsRecommended;

    public string Variant => Descriptor.Variant;

    public string WerDisplay => ModelMetricsFormatter.FormatWer(Descriptor.Research.Wer);

    public string WerTooltip => ModelMetricsFormatter.FormatWerDetail(Descriptor.Research.Wer);

    public string SpeedDisplay => ModelMetricsFormatter.FormatSpeed(Descriptor.Research.Speed);

    public string SizeDisplay => ModelMetricsFormatter.FormatSize(Descriptor.SizeBytes);

    public string LatencyDisplay => ModelMetricsFormatter.FormatLatency(Descriptor.Latency);
}
