using Microsoft.UI.Xaml.Controls;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Presentation;
using VoiceType.Uno.Services;

namespace VoiceType.Uno;

public sealed partial class MainPage : Page
{
    private const double DefaultTranslationHeight = 200;

    private double _translationRowHeight = DefaultTranslationHeight;

    public MainPage()
    {
        this.InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The row is only resized on visibility changes; apply the initial state too,
        // or with translation off the transcript loses the row's 200 px at startup.
        UpdateTranslationRowHeight();

#if DEBUG
        // Smoke-test hooks (Linux/WSLg verification): VOICETYPE_OPEN_MODEL_MANAGER=1
        // opens the model manager once the page has a XamlRoot, and
        // VOICETYPE_AUTO_DOWNLOAD_MODEL=<catalog folder> downloads one model through
        // the shared queue and logs the outcome.
        if (Environment.GetEnvironmentVariable("VOICETYPE_OPEN_MODEL_MANAGER") == "1"
            || Environment.GetEnvironmentVariable("VOICETYPE_AUTO_DOWNLOAD_MODEL") is { Length: > 0 })
            Loaded += OnSmokeTestLoaded;
#endif
    }

#if DEBUG
    private void OnSmokeTestLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnSmokeTestLoaded;

        if (Environment.GetEnvironmentVariable("VOICETYPE_AUTO_DOWNLOAD_MODEL") is { Length: > 0 } folder)
            _ = RunDownloadSmokeAsync(folder);

        if (Environment.GetEnvironmentVariable("VOICETYPE_OPEN_MODEL_MANAGER") == "1")
        {
            Console.WriteLine("[VoiceType.Uno] Smoke hook: opening the model manager");
            _ = ShowModelManagerAsync();
        }
    }

    /// <summary>Debug-only: downloads a catalog model through the shared queue and logs the outcome.</summary>
    private async Task RunDownloadSmokeAsync(string folderName)
    {
        var descriptor = ModelCatalog.FindBySubfolder(folderName);
        if (descriptor is null)
        {
            Console.WriteLine($"[SMOKE] Unknown catalog folder '{folderName}'");
            return;
        }

        var settings = ViewModel.CreateSettingsSnapshot();
        var root = string.IsNullOrWhiteSpace(settings.ModelsRootPath) ? AppPaths.ModelsDir : settings.ModelsRootPath;
        Console.WriteLine($"[SMOKE] {descriptor.Title} | repo={descriptor.RepoId} quant={descriptor.QuantizationFolder} root={root}");

        var queue = App.Services.GetRequiredService<DownloadQueueService>();
        var item = queue.EnqueueAsrModel(
            root,
            _ => { },
            repoId: descriptor.RepoId,
            quantizationFolder: descriptor.QuantizationFolder);

        var lastStatus = "";
        var watcher = Task.Run(async () =>
        {
            while (item.State is DownloadQueueItemState.Queued or DownloadQueueItemState.Running)
            {
                if (!string.Equals(item.Status, lastStatus, StringComparison.Ordinal))
                {
                    lastStatus = item.Status;
                    Console.WriteLine($"[SMOKE] {item.Percent:F1}% {lastStatus}");
                }

                await Task.Delay(2000);
            }
        });

        try
        {
            var path = await item.Completion;
            Console.WriteLine($"[SMOKE] COMPLETED {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SMOKE] FAILED {ex.GetType().Name}: {ex.Message}");
        }

        await watcher;
        Console.WriteLine($"[SMOKE] final state={item.State} bytes={item.DownloadedBytes}");
    }
#endif

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsTranslationVisible))
            UpdateTranslationRowHeight();
        else if (e.PropertyName == nameof(MainViewModel.FloatingText) && ViewModel.IsAutoScrollEnabled)
            DispatcherQueue.TryEnqueue(() => ScrollToEnd(TranscriptScroll));
        else if (e.PropertyName == nameof(MainViewModel.TranslatedText) && ViewModel.IsAutoScrollEnabled)
            DispatcherQueue.TryEnqueue(() => ScrollToEnd(TranslationScroll));
    }

    /// <summary>
    /// Auto-scroll a view to the newest text. Deferred to the dispatcher so layout
    /// has already grown the scrollable extent, and using double.MaxValue (which the
    /// ScrollViewer clamps to the real maximum) so no synchronous UpdateLayout()
    /// pass is required.
    /// </summary>
    private static void ScrollToEnd(ScrollViewer scroll)
    {
        if (scroll is null)
            return;
        scroll.ChangeView(null, double.MaxValue, null);
    }

    /// <summary>
    /// The translation row is pixel-sized so the divider can resize it. When
    /// translation is toggled off, collapse the row to zero so no empty band
    /// remains; when toggled on, restore the user's chosen height.
    /// </summary>
    private void UpdateTranslationRowHeight()
    {
        if (ViewModel.IsTranslationVisible)
        {
            TranslationRow.Height = new GridLength(Math.Max(80, _translationRowHeight));
        }
        else
        {
            if (TranslationRow.Height.IsAbsolute)
                _translationRowHeight = TranslationRow.Height.Value;
            TranslationRow.Height = new GridLength(0);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(ViewModel.CreateSettingsSnapshot())
        {
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();

        // The dialog closes itself when the user asks for the model manager, so
        // pending edits are saved first and the manager opens afterwards (WinUI
        // allows a single content dialog per XamlRoot at a time).
        if (result == ContentDialogResult.Primary || dialog.OpenModelManagerRequested)
            await ViewModel.ApplySettingsAsync(dialog.ViewModel.BuildSettings());

        if (dialog.OpenModelManagerRequested)
            await ShowModelManagerAsync();
    }

    private async void Models_Click(object sender, RoutedEventArgs e)
        => await ShowModelManagerAsync();

    private async Task ShowModelManagerAsync()
    {
        try
        {
            var dialog = new ModelManagerDialog(
                ViewModel.CreateSettingsSnapshot,
                ViewModel.ApplySettingsAsync)
            {
                XamlRoot = XamlRoot
            };

            await dialog.ShowAsync();
            dialog.ViewModel.Refresh();
        }
        catch (Exception ex)
        {
            // ContentDialog throws when another dialog is already open on the same
            // XamlRoot; report it instead of losing the exception in a discard.
            Console.WriteLine($"[VoiceType.Uno] Model manager could not be shown: {ex}");
        }
    }

    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HelpDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void Downloads_Click(object sender, RoutedEventArgs e)
    {
        // Single-window UX on every platform: the shared DownloadsView is shown
        // in a ContentDialog (Windows, desktop and Android alike).
        var view = new DownloadsView();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Content = view,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
        view.DetachViewModel();
    }

    private void DownloadAsrModel_Click(object sender, RoutedEventArgs e)
        => ViewModel.EnqueueAsrModelDownload();

    private void DownloadTranslationModel_Click(object sender, RoutedEventArgs e)
        => ViewModel.EnqueueTranslationModelDownload();

    // ── Resizable divider between transcript and translation ───────────────
    // GridSplitter (CommunityToolkit v7 Uno port) is not compatible with the
    // WinUI 3 head, so the divider is dragged manually: capture the pointer,
    // measured by dragging the divider: the transcript row is star-sized and
    // absorbs the space the translation row gains/loses.

    private bool _dividerDragging;
    private double _dividerStartY;
    private double _dividerStartTranslationHeight;

    private void Divider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement element)
            return;

        _dividerDragging = true;
        _dividerStartY = e.GetCurrentPoint(this).Position.Y;
        _dividerStartTranslationHeight = TranslationRow.Height.Value;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Divider_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dividerDragging)
            return;

        var currentY = e.GetCurrentPoint(this).Position.Y;
        var delta = currentY - _dividerStartY;

        // Dragging down grows the transcript, so the translation shrinks;
        // dragging up grows the translation. Clamp to sensible bounds.
        var newHeight = Math.Clamp(_dividerStartTranslationHeight - delta, 80, 400);
        TranslationRow.Height = new GridLength(newHeight);
        _translationRowHeight = newHeight;
        e.Handled = true;
    }

    private void Divider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement element)
            return;

        _dividerDragging = false;
        element.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }
}
