using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using SpeechLib.ModelDownload;

namespace VoiceType.WinUI.ViewModels;

/// <summary>
/// A catalog model shown as a card in the download manager: static badges from the
/// <see cref="ModelDescriptor"/> plus live state (installed, downloading with
/// progress, idle) so each card has its own Download button.
/// </summary>
public sealed partial class ModelCardViewModel : ObservableObject
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(CanDownload), nameof(InstalledVisibility))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(CanDownload), nameof(ProgressVisibility))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private double _progress;

    public bool CanDownload => !IsDownloading;

    public string ActionText => IsDownloading
        ? $"Downloading {Progress:F0}%"
        : IsInstalled ? "Re-download" : "Download";

    public Visibility InstalledVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;
}
