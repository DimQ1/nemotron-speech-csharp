using VoiceType.Uno.Presentation;

namespace VoiceType.Uno;

/// <summary>
/// Desktop host for the download queue UI. The content lives in the shared
/// <see cref="DownloadsView" /> control so Android (single-window) can show the
/// same UI inside a ContentDialog instead.
/// </summary>
public sealed partial class DownloadsWindow : Window
{
    public DownloadsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => View.DetachViewModel();
    }
}
