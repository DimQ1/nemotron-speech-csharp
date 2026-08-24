using Microsoft.UI.Dispatching;
using VoiceType.Uno.Presentation;
using VoiceType.Uno.Services;

namespace VoiceType.Uno;

public sealed partial class DownloadsWindow : Window
{
    public DownloadsViewModel ViewModel { get; }

    public DownloadsWindow()
    {
        InitializeComponent();
        ViewModel = new DownloadsViewModel(
            App.Services.GetRequiredService<DownloadQueueService>(),
            DispatcherQueue.GetForCurrentThread());
        Closed += (_, _) => ViewModel.Detach();
    }
}
