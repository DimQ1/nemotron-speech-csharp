using Microsoft.UI.Xaml.Controls;
using VoiceType.Uno.Presentation;

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
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsTranslationVisible))
            UpdateTranslationRowHeight();
        else if (e.PropertyName == nameof(MainViewModel.FloatingText) && ViewModel.IsAutoScrollEnabled)
            ScrollToEnd(TranscriptScroll);
        else if (e.PropertyName == nameof(MainViewModel.TranslatedText) && ViewModel.IsAutoScrollEnabled)
            ScrollToEnd(TranslationScroll);
    }

    /// <summary>Auto-scroll a view to the newest text.</summary>
    private static void ScrollToEnd(ScrollViewer scroll)
    {
        if (scroll is null)
            return;
        scroll.UpdateLayout();
        scroll.ChangeView(null, scroll.ScrollableHeight, null);
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

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.ApplySettingsAsync(dialog.ViewModel.BuildSettings());
    }

    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HelpDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        var window = new DownloadsWindow();
        window.Activate();
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
