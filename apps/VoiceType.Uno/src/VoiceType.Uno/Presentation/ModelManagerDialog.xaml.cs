using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// Model manager: lists every downloadable ASR model with its measured
/// accuracy/speed/size, shows which is installed and in use, and downloads,
/// switches or deletes them. Owned by the main page; the caller supplies the
/// settings snapshot/apply delegates so the manager can switch the active model.
/// </summary>
public sealed partial class ModelManagerDialog : ContentDialog
{
    public ModelManagerViewModel ViewModel { get; }

    public ModelManagerDialog(
        Func<AppSettings> settingsProvider,
        Func<AppSettings, Task> applySettings)
    {
        ViewModel = new ModelManagerViewModel(
            App.Services.GetRequiredService<DownloadQueueService>(),
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
            settingsProvider,
            applySettings);
        InitializeComponent();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void CancelAll_Click(object sender, RoutedEventArgs e) => ViewModel.CancelAll();

    private async void DownloadCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card)
            return;

        // An installed model is re-downloaded in place, which also repairs a
        // broken or partially downloaded folder.
        await card.DownloadAsync(force: card.IsInstalled);
    }

    private async void UseCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            await card.UseAsync();
    }

    private void DeleteCard_Click(object sender, RoutedEventArgs e)
        => CardOf(sender)?.Delete();

    private void CancelCard_Click(object sender, RoutedEventArgs e)
        => CardOf(sender)?.CancelDownload();

    /// <summary>The card a button inside the item template belongs to.</summary>
    private static ModelCardViewModel? CardOf(object sender)
        => (sender as FrameworkElement)?.DataContext as ModelCardViewModel;
}
