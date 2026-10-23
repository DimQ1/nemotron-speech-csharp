using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Presentation;
using VoiceType.Uno.Presentation.Chat;
using VoiceType.Uno.Presentation.Controls;
using VoiceType.Uno.Presentation.History;
using VoiceType.Uno.Presentation.Queue;
using VoiceType.Uno.Services;

namespace VoiceType.Uno;

public sealed partial class MainPage : Page
{
    private const double MaxPaneWidth = 320;
    private const double MaxPaneWidthRatio = 0.85;

    private MainLayout _layout;
    private bool _isKeyboardOpen;
    private bool _compactShowsTranslation;
    private bool _platformHooksAttached;

    public MainPage()
    {
        this.InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();

        // NavigationCacheMode="Required" keeps this instance across trips to the Models
        // page, so the singleton view model is subscribed exactly once.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += OnLoaded;
        BuildSourceChip();
        UpdateSourceIcons();

#if DEBUG
        // Smoke-test hook (Linux/WSLg verification): VOICETYPE_OPEN_MODEL_MANAGER=1 opens
        // the Models page once the page is loaded, and VOICETYPE_AUTO_DOWNLOAD_MODEL=<catalog
        // folder> downloads one model through the shared queue and logs the outcome.
        // VOICETYPE_SMOKE_RECORD_SECONDS=<n> records n seconds once the model is ready
        // (optional VOICETYPE_SMOKE_MUTE=<from>-<to> seconds mutes inside it) and logs the
        // History recording it saved.
        if (Environment.GetEnvironmentVariable("VOICETYPE_OPEN_MODEL_MANAGER") == "1"
            || Environment.GetEnvironmentVariable("VOICETYPE_AUTO_DOWNLOAD_MODEL") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("VOICETYPE_SMOKE_RECORD_SECONDS") is { Length: > 0 })
            Loaded += OnSmokeTestLoaded;
#endif
    }

    /// <summary>
    /// Wires the audio source chip. There are two buttons for the one chip (a row under the app bar and an icon in the
    /// app bar; <see cref="MainLayout.SourceChip"/> shows one of them), each with its own menu. Where only the
    /// microphone exists (Android) there is nothing to switch and neither is ever shown.
    /// </summary>
    private void BuildSourceChip()
    {
        if (!ViewModel.IsAudioSourceSwitchable)
            return;

        SourceChip.Flyout = CreateSourceMenu(Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft);
        SourceChipCompact.Flyout = CreateSourceMenu(Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight);
    }

    /// <summary>Draws the chip icon of the current source in both chip buttons (the row one and the app bar one).</summary>
    private void UpdateSourceIcons()
    {
        SourceChipIcon.Content = AudioSourceIcons.Create(ViewModel.AudioSourceGlyph);
        SourceChipCompactIcon.Content = AudioSourceIcons.Create(ViewModel.AudioSourceGlyph);
    }

    /// <summary>
    /// One checkable item per source, then (while recording) the hint that a switch starts a new recording. The checks
    /// are set each time the menu opens, so a change made in Settings is reflected too.
    /// </summary>
    private MenuFlyout CreateSourceMenu(Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode placement)
    {
        var items = new List<(string Value, ToggleMenuFlyoutItem Item)>();
        var menu = new MenuFlyout { Placement = placement };
        foreach (var option in ViewModel.AudioSourceOptions)
        {
            var value = option.Value;
            // 44 px high like every touch target of the screen (the default menu item is about 40).
            var item = new ToggleMenuFlyoutItem { Text = option.Title, IsEnabled = option.Enabled, MinHeight = 44 };
            item.Click += (_, _) => ViewModel.SelectAudioSource(value);
            items.Add((value, item));
            menu.Items.Add(item);
        }

        var separator = new MenuFlyoutSeparator();
        var hint = new MenuFlyoutItem { Text = SettingsText.AudioSourceSwitchWhileRecordingHint, IsEnabled = false, MinHeight = 44 };
        menu.Items.Add(separator);
        menu.Items.Add(hint);

        menu.Opening += (_, _) =>
        {
            foreach (var (value, item) in items)
                item.IsChecked = string.Equals(value, ViewModel.AudioSource, StringComparison.Ordinal);
            separator.Visibility = hint.Visibility = ViewModel.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        };
        return menu;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Models may have been downloaded or deleted on the Models page.
        if (e.NavigationMode == Microsoft.UI.Xaml.Navigation.NavigationMode.Back)
        {
            ViewModel.RefreshModelBanners();
            // Recordings may have been deleted on the History pages.
            _ = ViewModel.RefreshHistorySummaryAsync();
        }
    }

#if DEBUG
    private void OnSmokeTestLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnSmokeTestLoaded;

        if (Environment.GetEnvironmentVariable("VOICETYPE_AUTO_DOWNLOAD_MODEL") is { Length: > 0 } folder)
            _ = RunDownloadSmokeAsync(folder);

        if (double.TryParse(Environment.GetEnvironmentVariable("VOICETYPE_SMOKE_RECORD_SECONDS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            _ = RunRecordSmokeAsync(seconds, Environment.GetEnvironmentVariable("VOICETYPE_SMOKE_MUTE"));

        if (Environment.GetEnvironmentVariable("VOICETYPE_OPEN_MODEL_MANAGER") == "1")
        {
            Console.WriteLine("[VoiceType.Uno] Smoke hook: opening the Models page");
            OpenModelsPage(ModelsTab.Speech);
        }
    }

    /// <summary>Debug-only: records for a while (optionally muting inside it) and logs the saved History recording.</summary>
    private async Task RunRecordSmokeAsync(double seconds, string? mute)
    {
        try
        {
            for (var i = 0; i < 600 && !ViewModel.IsModelReady; i++)
                await Task.Delay(500);
            Console.WriteLine($"[SMOKE] model ready={ViewModel.IsModelReady}");
            if (!ViewModel.IsModelReady)
                return;

            double muteFrom = -1, muteTo = -1;
            if (mute?.Split('-') is [var from, var to])
            {
                muteFrom = double.Parse(from, System.Globalization.CultureInfo.InvariantCulture);
                muteTo = double.Parse(to, System.Globalization.CultureInfo.InvariantCulture);
            }

            await ViewModel.ToggleCommand.ExecuteAsync(null);
            Console.WriteLine($"[SMOKE] recording={ViewModel.IsRecording}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var t = clock.Elapsed.TotalSeconds;
                var shouldMute = t >= muteFrom && t < muteTo;
                if (shouldMute != ViewModel.IsCaptureMuted)
                {
                    ViewModel.ToggleMuteCommand.Execute(null);
                    Console.WriteLine($"[SMOKE] muted={ViewModel.IsCaptureMuted} at {t:F1}s");
                }
                await Task.Delay(100);
            }

            if (ViewModel.IsCaptureMuted)
                ViewModel.ToggleMuteCommand.Execute(null);
            await ViewModel.ToggleCommand.ExecuteAsync(null);
            Console.WriteLine("[SMOKE] stop requested");

            for (var i = 0; i < 300 && !ViewModel.IsSavedSnackbarVisible; i++)
                await Task.Delay(100);
            Console.WriteLine($"[SMOKE] saved={ViewModel.IsSavedSnackbarVisible} id={ViewModel.LastSavedRecordingId} status='{ViewModel.StatusText}' text='{ViewModel.FloatingText}'");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SMOKE] record smoke failed: {ex}");
        }
    }

    /// <summary>Debug-only: downloads a catalog model through the shared manager and logs the outcome.</summary>
    private async Task RunDownloadSmokeAsync(string folderName)
    {
        var descriptor = ModelCatalog.FindBySubfolder(folderName);
        if (descriptor is null)
        {
            Console.WriteLine($"[SMOKE] Unknown catalog folder '{folderName}'");
            return;
        }

        var settings = ViewModel.CreateSettingsSnapshot();
        var root = ModelStorageLayout.Resolve(settings).AsrDir;
        Console.WriteLine($"[SMOKE] {descriptor.Title} | repo={descriptor.RepoId} quant={descriptor.QuantizationFolder} root={root}");

        var downloads = App.Services.GetRequiredService<ModelDownloadManager>();
        var job = downloads.Enqueue(ModelDownloadRequest.ForCatalogModel(descriptor, root));

        var lastStatus = "";
        var watcher = Task.Run(async () =>
        {
            while (job.IsActive)
            {
                var status = $"{job.Percent:F0}% {job.FilesDone}/{job.FilesTotal} {job.CurrentFile}";
                if (!string.Equals(status, lastStatus, StringComparison.Ordinal))
                {
                    lastStatus = status;
                    Console.WriteLine($"[SMOKE] {job.Percent:F1}% {status}");
                }

                await Task.Delay(2000);
            }
        });

        try
        {
            var path = await job.Completion;
            Console.WriteLine($"[SMOKE] COMPLETED {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SMOKE] FAILED {ex.GetType().Name}: {ex.Message}");
        }

        await watcher;
        Console.WriteLine($"[SMOKE] final state={job.State} bytes={job.DownloadedBytes}");
    }
#endif

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachPlatformHooks();
        ApplyLayout();
        UpdateVisualStates(useTransitions: false);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsTranslationVisible):
                ApplyLayout();
                break;
            case nameof(MainViewModel.RecordingPhase):
            case nameof(MainViewModel.IsManualInputEnabled):
                UpdateVisualStates(useTransitions: true);
                break;
            case nameof(MainViewModel.AudioSourceGlyph):
                UpdateSourceIcons();
                break;
            case nameof(MainViewModel.FloatingText) when ViewModel.IsAutoScrollEnabled:
                DispatcherQueue.TryEnqueue(() => ScrollToEnd(TranscriptScroll));
                break;
            case nameof(MainViewModel.TranslatedText) when ViewModel.IsAutoScrollEnabled:
                DispatcherQueue.TryEnqueue(() => ScrollToEnd(TranslationScroll));
                break;
        }
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

    private void UpdateVisualStates(bool useTransitions)
    {
        var recordState = ViewModel.RecordingPhase switch
        {
            RecordingPhase.Recording => "RecordLive",
            RecordingPhase.Muted => "RecordMuted",
            _ => "RecordIdle"
        };
        VisualStateManager.GoToState(this, recordState, useTransitions);
        VisualStateManager.GoToState(this, ViewModel.IsManualInputEnabled ? "ManualInput" : "SpeechInput", useTransitions);
    }

    // ── Adaptive layout (design §5.6) ─────────────────────────────────────────

    /// <summary>
    /// Applies the adaptive state for the current window size, translation and keyboard.
    /// The state is computed by <see cref="MainLayout.Compute"/> (unit-tested in the core
    /// project) because it combines width, height and the keyboard, which AdaptiveTriggers
    /// cannot express.
    /// </summary>
    private void ApplyLayout()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        Shell.OpenPaneLength = Math.Min(MaxPaneWidth, width * MaxPaneWidthRatio);

        _layout = MainLayout.Compute(width, height, ViewModel.IsTranslationVisible, _isKeyboardOpen, ViewModel.IsAudioSourceSwitchable);
        ApplyChrome(_layout);
        ApplyCards(_layout.Cards);
    }

    private void ApplyChrome(MainLayout layout)
    {
        var compact = layout.IsCompactChrome;
        TopBar.MinHeight = compact ? 48 : 56;
        TopBar.Padding = compact ? new Thickness(4, 0, 8, 0) : new Thickness(4, 4, 8, 4);

        var recordSize = compact ? 56 : 72;
        RecordButton.Width = recordSize;
        RecordButton.Height = recordSize;
        RecordButton.CornerRadius = new CornerRadius(recordSize / 2.0);
        RecordCaptionText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BottomBar.Padding = compact ? new Thickness(16, 4, 16, 8) : new Thickness(16, 8, 16, 12);

        SourceChip.Visibility = layout.SourceChip == SourceChipPlacement.Row ? Visibility.Visible : Visibility.Collapsed;
        SourceChipCompact.Visibility = layout.SourceChip == SourceChipPlacement.TopBar ? Visibility.Visible : Visibility.Collapsed;

        var side = layout.SidePadding;
        BannerPanel.Padding = new Thickness(side, 0, side, 0);
        ContentGrid.Padding = new Thickness(side, 8, side, 8);

        // Short windows: one-line banners and a progress strip without its caption.
        foreach (var banner in new[] { AsrBannerText, TranslationBannerText })
        {
            banner.MaxLines = layout.IsCompactBanner ? 1 : 0;
            banner.TextWrapping = layout.IsCompactBanner ? TextWrapping.NoWrap : TextWrapping.Wrap;
        }
        QueueProgressCaption.Visibility = layout.IsCompactBanner ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyCards(CardArrangement cards)
    {
        // Defaults: one column, transcript in the first row, everything visible.
        var secondRowHeight = new GridLength(0);
        double secondRowMinHeight = 0;
        var secondColumnWidth = new GridLength(0);
        double rowSpacing = 0, columnSpacing = 0;
        int translationRow = 0, translationColumn = 0;
        var showTranscript = true;
        var showTranslation = true;
        var showTranslationBody = true;
        var showTabs = false;

        switch (cards)
        {
            case CardArrangement.TranscriptOnly:
                showTranslation = false;
                break;
            case CardArrangement.Stacked:
                secondRowHeight = new GridLength(0.8, GridUnitType.Star);
                secondRowMinHeight = 96;
                rowSpacing = 8;
                translationRow = 1;
                break;
            case CardArrangement.SideBySide:
                secondColumnWidth = new GridLength(1, GridUnitType.Star);
                columnSpacing = 8;
                translationColumn = 1;
                break;
            case CardArrangement.Tabbed:
                showTabs = true;
                showTranscript = !_compactShowsTranslation;
                showTranslation = _compactShowsTranslation;
                break;
            case CardArrangement.TranslationCollapsed:
                secondRowHeight = GridLength.Auto;
                rowSpacing = 8;
                translationRow = 1;
                showTranslationBody = false;
                break;
        }

        FirstCardRow.MinHeight = cards == CardArrangement.Stacked ? 96 : 0;
        SecondCardRow.Height = secondRowHeight;
        SecondCardRow.MinHeight = secondRowMinHeight;
        SecondCardColumn.Width = secondColumnWidth;
        ContentGrid.RowSpacing = rowSpacing;
        ContentGrid.ColumnSpacing = columnSpacing;

        Grid.SetRow(TranslationCard, translationRow);
        Grid.SetColumn(TranslationCard, translationColumn);
        TranscriptCard.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
        TranslationCard.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        TranslationBody.Visibility = showTranslationBody ? Visibility.Visible : Visibility.Collapsed;
        TranslationHeader.BorderThickness = showTranslationBody ? new Thickness(0, 0, 0, 1) : new Thickness(0);

        // Compact: the segmented switch sits over the card header, so the header
        // titles are hidden (kept in layout so the header keeps its height).
        CompactTabs.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
        var titleOpacity = showTabs ? 0 : 1;
        TranscriptTitle.Opacity = titleOpacity;
        TranslationTitle.Opacity = titleOpacity;
        TranslationTargetCombo.Visibility = showTabs ? Visibility.Collapsed : Visibility.Visible;
        var headerHeight = showTabs ? 52 : 44;
        TranscriptHeader.MinHeight = headerHeight;
        TranslationHeader.MinHeight = headerHeight;
        TranscriptTab.IsChecked = !_compactShowsTranslation;
        TranslationTab.IsChecked = _compactShowsTranslation;
    }

    private void CompactTab_Click(object sender, RoutedEventArgs e)
    {
        _compactShowsTranslation = ReferenceEquals(sender, TranslationTab);
        ApplyCards(_layout.Cards);
    }

    // ── Platform hooks: on-screen keyboard and system back ────────────────────

    /// <summary>
    /// Subscribes to the input pane (Keyboard state) and the system back button (closes
    /// the menu first). Both are per-view services that heads without them (WinUI 3
    /// desktop, some Skia hosts) report by throwing, so each is guarded.
    /// </summary>
    private void AttachPlatformHooks()
    {
        if (_platformHooksAttached)
            return;
        _platformHooksAttached = true;

#if !VOICE_TYPE_WINDOWS
        try
        {
            var inputPane = Windows.UI.ViewManagement.InputPane.GetForCurrentView();
            inputPane.Showing += (_, _) => SetKeyboardOpen(true);
            inputPane.Hiding += (_, _) => SetKeyboardOpen(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Input pane unavailable: {ex.Message}");
        }

        try
        {
            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] System back unavailable: {ex.Message}");
        }
#endif
    }

    private void SetKeyboardOpen(bool isOpen)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isKeyboardOpen == isOpen)
                return;
            _isKeyboardOpen = isOpen;
            ApplyLayout();
        });
    }

#if !VOICE_TYPE_WINDOWS
    private void OnBackRequested(object? sender, Windows.UI.Core.BackRequestedEventArgs e)
    {
        if (!Shell.IsPaneOpen)
            return;

        Shell.IsPaneOpen = false;
        e.Handled = true;
    }
#endif

    // ── Menu ──────────────────────────────────────────────────────────────────

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        Shell.IsPaneOpen = true;
        _ = ViewModel.RefreshHistorySummaryAsync();
        _ = RefreshChatsSummaryAsync();
    }

    private void CloseMenu_Click(object sender, RoutedEventArgs e) => Shell.IsPaneOpen = false;

    /// <summary>
    /// Closes the menu before a dialog or page opens: one content dialog per XamlRoot,
    /// and the SplitView's light-dismiss layer must not stay under the dialog.
    /// </summary>
    private async Task ClosePaneAsync()
    {
        if (!Shell.IsPaneOpen)
            return;

        var closed = new TaskCompletionSource();
        void OnPaneClosed(SplitView sender, object args) => closed.TrySetResult();

        Shell.PaneClosed += OnPaneClosed;
        try
        {
            Shell.IsPaneOpen = false;
            // Some heads skip the close animation; never wait for long.
            await Task.WhenAny(closed.Task, Task.Delay(400));
        }
        finally
        {
            Shell.PaneClosed -= OnPaneClosed;
        }
    }

    private async void SpeechModels_Click(object sender, RoutedEventArgs e)
    {
        await ClosePaneAsync();
        OpenModelsPage(ModelsTab.Speech);
    }

    private async void Downloads_Click(object sender, RoutedEventArgs e)
    {
        await ClosePaneAsync();
        OpenModelsPage(ModelsTab.Downloads);
    }

    private void OpenModelsPage(ModelsTab tab) => Frame?.Navigate(typeof(ModelsPage), tab);

    // ── Recordings history ────────────────────────────────────────────────────

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        await ClosePaneAsync();
        Frame?.Navigate(typeof(HistoryPage));
    }

    // Chats (design voicetype-uno-history §4.4): the second menu section.
    private async void Chats_Click(object sender, RoutedEventArgs e)
    {
        await ClosePaneAsync();
        Frame?.Navigate(typeof(ChatsPage));
    }

    /// <summary>Second line of the Chats menu entry ("3 chats"), counted off the UI thread.</summary>
    private async Task RefreshChatsSummaryAsync()
    {
        try
        {
            var chats = App.Services.GetRequiredService<VoiceType.Uno.History.ChatStore>();
            var count = await Task.Run(() => chats.LoadAll().Chats.Count);
            ChatsSummaryText.Text = VoiceType.Uno.History.ChatUiFormat.MenuSummary(count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ChatsSummaryText.Text = "Ask Gemma across recordings";
        }
    }

    private async void Queue_Click(object sender, RoutedEventArgs e)
    {
        await ClosePaneAsync();
        Frame?.Navigate(typeof(QueuePage));
    }

    /// <summary>"Saved to History · Open": opens the recording that was just saved.</summary>
    private void SnackbarOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.TakeSavedRecordingToOpen() is { } recordingId)
            Frame?.Navigate(typeof(RecordingPage), recordingId);
    }

    // Settings is a page (design voicetype-uno-settings): hub on narrow windows, two panes on wide ones.
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await ClosePaneAsync();
        Frame?.Navigate(typeof(VoiceType.Uno.Presentation.SettingsUi.SettingsPage));
    }

    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClosePaneAsync();
            var dialog = new HelpDialog { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Help could not be shown: {ex}");
        }
    }

    // ── Banners ───────────────────────────────────────────────────────────────

    private void BannerText_Tapped(object sender, TappedRoutedEventArgs e)
        => OpenModelsPage(ModelsTab.Downloads);

    private void DownloadAsrModel_Click(object sender, RoutedEventArgs e)
        => ViewModel.EnqueueAsrModelDownload();

    private void DownloadTranslationModel_Click(object sender, RoutedEventArgs e)
        => ViewModel.EnqueueTranslationModelDownload();
}
