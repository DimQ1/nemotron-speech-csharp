using Microsoft.UI.Xaml.Controls;
using VoiceType.Uno.Presentation;

namespace VoiceType.Uno;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

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
    // measure the vertical delta, and resize the translation row's MaxHeight
    // (the transcript row is star-sized and absorbs the freed space).

    private bool _dividerDragging;
    private double _dividerStartY;
    private double _dividerStartTranslationHeight;

    private void Divider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement element)
            return;

        _dividerDragging = true;
        _dividerStartY = e.GetCurrentPoint(this).Position.Y;
        _dividerStartTranslationHeight = TranslationRow.MaxHeight > 0
            ? TranslationRow.MaxHeight
            : 400; // default cap from XAML
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Divider_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dividerDragging)
            return;

        var currentY = e.GetCurrentPoint(this).Position.Y;
        var delta = currentY - _dividerStartY;

        // Dragging down increases the transcript, so the translation shrinks;
        // dragging up grows the translation. Clamp to the XAML min/max.
        var newHeight = Math.Clamp(_dividerStartTranslationHeight - delta, 80, 400);
        TranslationRow.MaxHeight = newHeight;
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
