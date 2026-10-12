using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SpeechLib.ModelDownload;
using Windows.System;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// Full-screen "Models" page with three tabs: speech models of the catalog,
/// the on-device translation model and the shared download queue. Opened with
/// <c>Frame.Navigate(typeof(ModelsPage), ModelsTab.X)</c>; the back button, Escape
/// and the system back gesture return to the main page. Downloads keep running
/// after the page closes — they live in the app-wide <see cref="ModelDownloadManager"/>.
/// </summary>
public sealed partial class ModelsPage : Page
{
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public ModelsPage()
    {
        var main = App.Services.GetRequiredService<MainViewModel>();
        var downloads = App.Services.GetRequiredService<ModelDownloadManager>();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        Models = new ModelManagerViewModel(downloads, dispatcher, main.CreateSettingsSnapshot, main.ApplySettingsAsync);
        TranslationModel = new TranslationModelCardViewModel(downloads, dispatcher, main.EnqueueTranslationModelDownload);
        Downloads = new DownloadsViewModel(downloads, dispatcher);
        TranslationModel.PropertyChanged += OnTranslationPropertyChanged;

        InitializeComponent();
    }

    /// <summary>Speech tab: catalog cards.</summary>
    public ModelManagerViewModel Models { get; }

    /// <summary>Translation tab: the LiteRT-LM model.</summary>
    public TranslationModelCardViewModel TranslationModel { get; }

    /// <summary>Downloads tab: the shared queue.</summary>
    public DownloadsViewModel Downloads { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SelectTab(e.Parameter is ModelsTab tab ? tab : ModelsTab.Speech);
        SubscribeSystemBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UnsubscribeSystemBack();

        // The page is not cached: drop every subscription to the app-wide download manager.
        TranslationModel.PropertyChanged -= OnTranslationPropertyChanged;
        Models.Detach();
        TranslationModel.Detach();
        Downloads.Detach();
    }

    // ── Navigation ────────────────────────────────────────────────────────

    private void GoBack()
    {
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    // Escape is a keyboard accelerator of the back button; this covers the
    // hardware GoBack key when focus is inside the page.
    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.GoBack)
        {
            e.Handled = true;
            GoBack();
        }
    }

    /// <summary>
    /// Android back button / gesture (and the Skia desktop equivalent). The WinUI 3
    /// head has no CoreWindow, so SystemNavigationManager is not available there.
    /// </summary>
    private void SubscribeSystemBack()
    {
#if !VOICE_TYPE_WINDOWS
        try
        {
            _systemNavigation = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            _systemNavigation.BackRequested += OnSystemBackRequested;
        }
        catch (Exception ex)
        {
            _systemNavigation = null;
            Console.WriteLine($"[VoiceType.Uno] System back is not available: {ex.Message}");
        }
#endif
    }

    private void UnsubscribeSystemBack()
    {
#if !VOICE_TYPE_WINDOWS
        if (_systemNavigation is not null)
            _systemNavigation.BackRequested -= OnSystemBackRequested;
        _systemNavigation = null;
#endif
    }

#if !VOICE_TYPE_WINDOWS
    private void OnSystemBackRequested(object? sender, Windows.UI.Core.BackRequestedEventArgs e)
    {
        if (e.Handled || Frame?.CanGoBack != true)
            return;

        e.Handled = true;
        Frame.GoBack();
    }
#endif

    // Focus inside the page so Escape (the back button's accelerator) works at once.
    private void Page_Loaded(object sender, RoutedEventArgs e)
        => BackButton.Focus(FocusState.Programmatic);

    // ── Layout ────────────────────────────────────────────────────────────

    /// <summary>Page width from which speech cards sit in two columns (design doc §5.8).</summary>
    private const double TwoColumnWidth = 840;

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyCardColumns();

    private void SpeechCards_Loaded(object sender, RoutedEventArgs e) => ApplyCardColumns();

    private void ApplyCardColumns()
    {
        if (SpeechCards?.ItemsPanelRoot is Controls.CardColumnsPanel panel)
            panel.MaxColumns = ActualWidth >= TwoColumnWidth ? 2 : 1;
    }

    // ── Tabs ──────────────────────────────────────────────────────────────

    private void SelectTab(ModelsTab tab)
    {
        var button = tab switch
        {
            ModelsTab.Translation => TranslationTab,
            ModelsTab.Downloads => DownloadsTab,
            _ => SpeechTab
        };

        if (button.IsChecked == true)
            ShowPane(tab);
        else
            button.IsChecked = true; // raises Tab_Checked
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        var tab = ReferenceEquals(sender, TranslationTab) ? ModelsTab.Translation
            : ReferenceEquals(sender, DownloadsTab) ? ModelsTab.Downloads
            : ModelsTab.Speech;
        ShowPane(tab);
    }

    private void ShowPane(ModelsTab tab)
    {
        // Checked can fire while the template is applied, before named parts exist.
        if (SpeechPane is null)
            return;

        SpeechPane.Visibility = tab == ModelsTab.Speech ? Visibility.Visible : Visibility.Collapsed;
        TranslationPane.Visibility = tab == ModelsTab.Translation ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPane.Visibility = tab == ModelsTab.Downloads ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ChangeView(null, 0, null, disableAnimation: true);

        if (tab == ModelsTab.Translation)
            TranslationModel.Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Models.Refresh();
        TranslationModel.Refresh();
    }

    // The subtitle counts the translation model too.
    private void OnTranslationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslationModelCardViewModel.IsInstalled))
            Models.Refresh();
    }

    // ── Speech cards ──────────────────────────────────────────────────────

    private async void DownloadCard_Click(object sender, RoutedEventArgs e)
    {
        // Not installed: a partial folder is resumed rather than wiped.
        if (CardOf(sender) is { } card)
            await card.DownloadAsync(force: false);
    }

    /// <summary>The plain primary action: Cancel while downloading, otherwise Use.</summary>
    private async void PrimaryCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card)
            return;

        if (card.IsDownloading)
            card.CancelDownload();
        else if (card.CanUse)
            await card.UseAsync();
    }

    /// <summary>"⋯": rare actions, built per click so the items match the card's current state.</summary>
    private void MoreCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card || sender is not FrameworkElement anchor)
            return;

        var menu = new MenuFlyout();

        if (card.CanRedownload)
        {
            // Starts from an empty folder, which also repairs a broken install.
            var redownload = new MenuFlyoutItem { Text = "Re-download", Icon = new SymbolIcon(Symbol.Download) };
            redownload.Click += async (_, _) => await card.DownloadAsync(force: true);
            menu.Items.Add(redownload);
        }

        if (card.CanDelete)
        {
            var delete = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
            delete.Click += async (_, _) =>
            {
                if (await ConfirmDeleteAsync(card.Title, "The model files are removed from this device. You can download it again later."))
                    card.Delete();
            };
            menu.Items.Add(delete);
        }

        var details = new MenuFlyoutItem
        {
            Text = card.IsDetailsExpanded ? "Hide details" : "Show details",
            Icon = new SymbolIcon(Symbol.View)
        };
        details.Click += (_, _) => card.IsDetailsExpanded = !card.IsDetailsExpanded;
        menu.Items.Add(details);

        menu.ShowAt(anchor);
    }

    /// <summary>The card a button inside the item template belongs to.</summary>
    private static ModelCardViewModel? CardOf(object sender)
        => (sender as FrameworkElement)?.DataContext as ModelCardViewModel;

    // ── Translation card ──────────────────────────────────────────────────

    private void DownloadTranslation_Click(object sender, RoutedEventArgs e) => TranslationModel.Download();

    private void CancelTranslation_Click(object sender, RoutedEventArgs e) => TranslationModel.Cancel();

    private async void DeleteTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDeleteAsync(TranslationModel.Title, "Live translation stops working until the model is downloaded again."))
        {
            TranslationModel.Delete();
            Models.Refresh();
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string title, string consequence)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Delete {title}?",
                Content = new TextBlock { Text = consequence, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // ContentDialog throws when another dialog is already open on the same XamlRoot.
            Console.WriteLine($"[VoiceType.Uno] Delete confirmation could not be shown: {ex.Message}");
            return false;
        }
    }
}
