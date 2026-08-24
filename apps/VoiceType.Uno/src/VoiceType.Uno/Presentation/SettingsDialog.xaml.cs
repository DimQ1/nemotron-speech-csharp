using Microsoft.UI.Xaml.Controls;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsViewModel ViewModel { get; }

    public SettingsDialog(AppSettings settings)
    {
        ViewModel = new SettingsViewModel(settings);
        InitializeComponent();
    }

    private async void DownloadModel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.IsDownloadingModel = true;
        ViewModel.DownloadStatus = "Downloading model...";
        try
        {
            var downloader = App.Services.GetRequiredService<ModelDownloadService>();
            downloader.ProgressChanged += OnProgress;
            try
            {
                var modelPath = await downloader.DownloadRecommendedAsync(ViewModel.ModelsRootPath);
                ViewModel.SelectedModel = Path.GetFileName(modelPath);
                ViewModel.DownloadStatus = $"Downloaded to {modelPath}";
            }
            finally
            {
                downloader.ProgressChanged -= OnProgress;
            }
        }
        catch (Exception ex)
        {
            ViewModel.DownloadStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            ViewModel.IsDownloadingModel = false;
        }

        void OnProgress(ModelDownloadProgress progress)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ViewModel.DownloadStatus = progress.TotalFiles > 0
                    ? $"Downloading model... {progress.OverallProgress:F0}% ({progress.DownloadedFiles}/{progress.TotalFiles})"
                    : "Downloading model...";
            });
        }
    }

    private async void DownloadTranslationModel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.IsDownloadingModel = true;
        ViewModel.DownloadStatus = "Downloading translation model...";
        try
        {
            var downloader = App.Services.GetRequiredService<ModelDownloadService>();
            downloader.ProgressChanged += OnProgress;
            try
            {
                var modelPath = await downloader.DownloadTranslationModelAsync();
                ViewModel.DownloadStatus = $"Downloaded to {modelPath}";
                ViewModel.NotifyNativeModelChanged();
            }
            finally
            {
                downloader.ProgressChanged -= OnProgress;
            }
        }
        catch (Exception ex)
        {
            ViewModel.DownloadStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            ViewModel.IsDownloadingModel = false;
        }

        void OnProgress(ModelDownloadProgress progress)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ViewModel.DownloadStatus = progress.TotalBytes > 0
                    ? $"Downloading translation model... {progress.OverallProgress:F0}%"
                    : "Downloading translation model...";
            });
        }
    }
}
