using Microsoft.UI.Dispatching;
using VoiceType.Uno.Presentation;
using VoiceType.Uno.Services;

namespace VoiceType.Uno;

/// <summary>
/// Model download queue UI, shown inside a ContentDialog from the main page on
/// every platform (Windows, desktop and Android). Owns its DownloadsViewModel;
/// the caller must call <see cref="DetachViewModel"/> when the host closes so the
/// view model unsubscribes from the queue service.
/// </summary>
public sealed partial class DownloadsView : UserControl
{
    public DownloadsViewModel ViewModel { get; }

    public DownloadsView()
    {
        InitializeComponent();
        ViewModel = new DownloadsViewModel(
            App.Services.GetRequiredService<DownloadQueueService>(),
            DispatcherQueue.GetForCurrentThread());
    }

    /// <summary>Detaches the view model from the download queue service.</summary>
    public void DetachViewModel() => ViewModel.Detach();
}
