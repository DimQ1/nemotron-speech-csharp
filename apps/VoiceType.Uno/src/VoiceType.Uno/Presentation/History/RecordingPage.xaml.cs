using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using VoiceType.Uno.History;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Recording detail (design §4.3). Opened with
/// <c>Frame.Navigate(typeof(RecordingPage), recordingId)</c>, full screen below 840 px or
/// inside <see cref="HistoryPage"/>'s detail pane (<see cref="IsEmbedded"/>).
/// </summary>
public sealed partial class RecordingPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _reloadTimer;
    private string? _loadedAudio;
    private int _currentSegment = -1;
    private bool _isEmbedded;
    private bool _suppressDiffPicker;
    private HistoryDiffView? _diff;
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public RecordingPage()
    {
        ViewModel = new RecordingViewModel(HistoryServices.Recordings, HistoryServices.Chats);
        InitializeComponent();
        // Translate tab: a tap on a bilingual row moves the player (the tab shows the player too).
        TranslateView.SeekRequested += seconds => Player.Seek(TimeSpan.FromSeconds(seconds));
        _reloadTimer = _dispatcher.CreateTimer();
        _reloadTimer.Interval = TimeSpan.FromMilliseconds(400);
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += async (_, _) =>
        {
            if (!ViewModel.IsEditing && RecordingId.Length > 0)
                await ReloadAsync(ViewModel.ViewedVersionId);
        };
    }

    public RecordingViewModel ViewModel { get; }

    public string RecordingId => ViewModel.RecordingId;

    /// <summary>Hosted in the History detail pane: no back button, no system-back handling.</summary>
    public bool IsEmbedded
    {
        get => _isEmbedded;
        set
        {
            _isEmbedded = value;
            BackButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            BackColumn.Width = value ? new GridLength(8) : new GridLength(48);
            if (value)
                UnsubscribeSystemBack();
        }
    }

    /// <summary>The embedded page asks its host to close it (recording deleted).</summary>
    public event EventHandler? CloseRequested;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        HistoryServices.Recordings.Changed += OnStoreChanged;
        HistoryServices.Deletes.Changed += OnDeletesChanged;
        if (e.Parameter is string id && id.Length > 0)
            await ReloadAsync(null, id);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        HistoryServices.Recordings.Changed -= OnStoreChanged;
        HistoryServices.Deletes.Changed -= OnDeletesChanged;
        UnsubscribeSystemBack();
        _reloadTimer.Stop();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Player.PositionChanged += OnPlayerPosition;
        if (!_isEmbedded)
            SubscribeSystemBack();
        ApplyLayout();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        Player.PositionChanged -= OnPlayerPosition;
        // Embedded pages are dropped by the host without a navigation event.
        HistoryServices.Recordings.Changed -= OnStoreChanged;
        HistoryServices.Deletes.Changed -= OnDeletesChanged;
        UnsubscribeSystemBack();
        _reloadTimer.Stop();
    }

    private async Task ReloadAsync(string? viewVersion, string? id = null)
    {
        await ViewModel.LoadAsync(id ?? RecordingId, viewVersion);
        await TranslateView.LoadAsync(ViewModel.RecordingId, ViewModel.ViewedVersionId);
        _currentSegment = -1;
        if (ViewModel.AudioPath is { } audio && audio != _loadedAudio)
        {
            _loadedAudio = audio;
            await Player.LoadAsync(audio);
            // Segment ticks under the seek bar (fractions of the audio duration).
            if (ViewModel.ViewedTranscript is { } viewed && Player.Duration > TimeSpan.Zero)
                Player.SetTicks(VoiceType.Uno.Services.Playback.PlaybackCursor.SegmentTicks(viewed.Segments, Player.Duration));
        }
        if (ViewModel.IsDiffView)
            await RenderDiffAsync();
    }

    private void OnStoreChanged(string id)
    {
        if (id != RecordingId)
            return;
        _dispatcher.TryEnqueue(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    private void OnDeletesChanged() => ViewModel.RefreshVersions();

    // ── Layout ────────────────────────────────────────────────────────────

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    /// <summary>Low windows (landscape phones): a 48 px header and tighter tab margins keep the transcript readable.</summary>
    private void ApplyLayout()
    {
        var isShort = ActualHeight > 0 && ActualHeight < 480;
        Header.MinHeight = isShort ? 48 : 56;
        Header.Padding = isShort ? new Thickness(4, 0, 4, 0) : new Thickness(4);
        TabsBar.Margin = isShort ? new Thickness(16, 0, 16, 4) : new Thickness(16, 0, 16, 8);
        ChangedLabel.Visibility = ActualWidth < 360 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Tabs ──────────────────────────────────────────────────────────────

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.Tab = ReferenceEquals(sender, ChatsTab) ? RecordingTab.Chats
            : ReferenceEquals(sender, TranslateTab) ? RecordingTab.Translate
            : ReferenceEquals(sender, VersionsTab) ? RecordingTab.Versions
            : RecordingTab.Transcript;
    }

    private void SelectTab(RecordingTab tab)
    {
        var button = tab switch
        {
            RecordingTab.Chats => ChatsTab,
            RecordingTab.Translate => TranslateTab,
            RecordingTab.Versions => VersionsTab,
            _ => TranscriptTab,
        };
        button.IsChecked = true;
        ViewModel.Tab = tab;
    }

    // ── Transcript + player ───────────────────────────────────────────────

    private void Segment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SegmentViewModel segment)
            return;
        Player.Seek(TimeSpan.FromSeconds(segment.Start));
        Highlight(segment.Position, scroll: false);
    }

    private void OnPlayerPosition(TimeSpan position)
    {
        if (ViewModel.IsTranscriptTab)
            Highlight(ViewModel.SegmentAt(position.TotalSeconds), scroll: true);
        else if (ViewModel.IsTranslateTab)
            TranslateView.OnPlayerPosition(position);
    }

    private void Highlight(int index, bool scroll)
    {
        if (index == _currentSegment)
            return;
        if (_currentSegment >= 0 && _currentSegment < ViewModel.Segments.Count)
            ViewModel.Segments[_currentSegment].IsCurrent = false;
        _currentSegment = index;
        if (index < 0 || index >= ViewModel.Segments.Count)
            return;
        ViewModel.Segments[index].IsCurrent = true;
        if (scroll && SegmentsList.ContainerFromIndex(index) is UIElement container)
        {
            try
            {
                container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Follow scroll failed: {ex.Message}");
            }
        }
    }

    private void VersionPicker_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Manifest is not { } manifest)
            return;
        var menu = new MenuFlyout();
        foreach (var version in manifest.Versions.OrderByDescending(v => RecordingViewModel.VersionNumber(v.Id)))
        {
            var label = HistoryUiFormat.VersionShort(version) + (version.Id == manifest.CurrentVersion ? " · current" : "");
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = version.Id == ViewModel.ViewedVersionId, MinHeight = 44 };
            var id = version.Id;
            item.Click += async (_, _) => await ReloadAsync(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var all = new MenuFlyoutItem { Text = "All versions…", MinHeight = 44 };
        all.Click += (_, _) => SelectTab(RecordingTab.Versions);
        menu.Items.Add(all);
        menu.ShowAt((FrameworkElement)sender);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewedTranscript is not { } transcript)
            return;
        var package = new DataPackage();
        package.SetText(TranscriptDocument.JoinSegments(transcript.Segments));
        Clipboard.SetContent(package);
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportAsync(ViewModel.ViewedVersionId);

    private async Task ExportAsync(string? versionId)
    {
        if (await ExportDialog.CreateAsync(RecordingId, versionId) is { } dialog)
            await dialog.RunAsync(XamlRoot);
    }

    // ── Header ⋯ ──────────────────────────────────────────────────────────

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Rename", Symbol.Rename, RenameAsync));
        var edit = Item("Edit transcript", Symbol.Edit, () => { BeginEdit(); return Task.CompletedTask; });
        edit.IsEnabled = ViewModel.HasSegments;
        menu.Items.Add(edit);
        menu.Items.Add(Item("Export…", Symbol.Save, () => ExportAsync(ViewModel.ViewedVersionId)));
        var reprocess = Item("Re-recognize…", Symbol.Refresh, ReprocessAsync);
        reprocess.IsEnabled = ViewModel.HasAudio;
        menu.Items.Add(reprocess);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete", Symbol.Delete, DeleteRecordingAsync));
        menu.ShowAt((FrameworkElement)sender);
    }

    private static MenuFlyoutItem Item(string text, Symbol icon, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon), MinHeight = 44 };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Recording action '{text}' failed: {ex}");
            }
        };
        return item;
    }

    private async Task RenameAsync()
    {
        if (ViewModel.Manifest is not { } manifest)
            return;
        var title = await HistoryDialogs.RenameAsync(XamlRoot, manifest);
        if (title is not null)
            await ViewModel.RenameAsync(title);
    }

    private async Task ReprocessAsync() =>
        await HistoryDialogs.ShowSafeAsync(new ReprocessSheet([RecordingId]) { XamlRoot = XamlRoot });

    private async void Reprocess_Click(object sender, RoutedEventArgs e) => await ReprocessAsync();

    private async Task DeleteRecordingAsync()
    {
        var outcome = await HistoryDialogs.DeleteRecordingsAsync(XamlRoot, [RecordingId], ViewModel.AudioBytes, ViewModel.ChatCount);
        if (outcome == DeleteOutcome.Cancelled)
            return;
        Leave();
    }

    /// <summary>Closes the page after the recording is gone.</summary>
    private void Leave()
    {
        if (_isEmbedded)
            CloseRequested?.Invoke(this, EventArgs.Empty);
        else if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    // ── Edit mode ─────────────────────────────────────────────────────────

    private void Edit_Click(object sender, RoutedEventArgs e) => BeginEdit();

    private void BeginEdit()
    {
        if (!ViewModel.HasSegments)
            return;
        SelectTab(RecordingTab.Transcript);
        ViewModel.BeginEdit();
        EditList.ItemsSource = ViewModel.Segments;
    }

    private void EditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: SegmentViewModel segment } box && ViewModel.IsEditing)
            ViewModel.SetEditText(segment, box.Text);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => ViewModel.UndoEdit();

    private async void CancelEdit_Click(object sender, RoutedEventArgs e) => await CancelEditAsync();

    private async Task<bool> CancelEditAsync()
    {
        if (ViewModel.HasEditChanges
            && !await HistoryDialogs.AskAsync(XamlRoot, "Discard changes?",
                "Your edits are not saved. The transcript stays as it was.", "Discard", "Keep editing"))
            return false;
        ViewModel.CancelEdit();
        EditList.ItemsSource = null;
        return true;
    }

    private async void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SaveEditAsync();
            EditList.ItemsSource = null;
        }
        catch (Exception ex)
        {
            await HistoryDialogs.ShowMessageAsync(XamlRoot, "The edit could not be saved", ex.Message);
        }
    }

    // ── Versions ──────────────────────────────────────────────────────────

    private static VersionCardViewModel? CardOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as VersionCardViewModel;

    private async void MakeCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            await ViewModel.MakeCurrentAsync(card.Id);
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card)
            return;
        var reference = ViewModel.ReferenceOf(card.Id)
            ?? ViewModel.Manifest?.Versions.Select(v => v.Id).FirstOrDefault(v => v != card.Id);
        if (reference is null)
        {
            await HistoryDialogs.ShowMessageAsync(XamlRoot, "Nothing to compare", "This recording has only one version.");
            return;
        }
        _suppressDiffPicker = true;
        DiffFrom.SelectedItem = ViewModel.VersionChoices.FirstOrDefault(c => c.Id == reference);
        DiffTo.SelectedItem = ViewModel.VersionChoices.FirstOrDefault(c => c.Id == card.Id);
        _suppressDiffPicker = false;
        ViewModel.IsDiffMode = true;
        await RenderDiffAsync();
    }

    private void VersionMore_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("View transcript", Symbol.View, async () =>
        {
            SelectTab(RecordingTab.Transcript);
            await ReloadAsync(card.Id);
        }));
        menu.Items.Add(Item("Export…", Symbol.Save, () => ExportAsync(card.Id)));
        var delete = Item(card.IsCurrent ? "Delete (make another version current first)" : "Delete version",
            Symbol.Delete, () => DeleteVersionAsync(card));
        delete.IsEnabled = !card.IsCurrent;
        menu.Items.Add(delete);
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task DeleteVersionAsync(VersionCardViewModel card)
    {
        if (ViewModel.Manifest is not { } manifest || card.IsCurrent)
            return;
        var recordingId = RecordingId;
        var versionId = card.Id;

        if (!HistoryServices.HistorySettings.ConfirmDelete)
        {
            HistoryServices.Deletes.Schedule($"Version {versionId} deleted", [],
                [HistoryUiPendingDeletes.VersionKey(recordingId, versionId)],
                () => HistoryServices.DeleteVersionNow(recordingId, versionId));
            return;
        }

        var (languages, chats) = await Task.Run(() =>
        {
            var translations = HistoryServices.Recordings.GetDependentTranslations(recordingId, versionId)
                .Select(t => t.TargetLanguage).Distinct().ToList();
            var referencing = HistoryServices.Chats.ListReferencing(recordingId, versionId).Count;
            return (translations, referencing);
        });
        var text = HistoryUiDeleteText.Version(versionId, manifest.CurrentVersion ?? "", languages, chats);
        if (!await HistoryDialogs.ConfirmDeleteAsync(XamlRoot, text, allowDontAsk: true))
            return;
        try
        {
            await Task.Run(() => HistoryServices.DeleteVersionNow(recordingId, versionId));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or KeyNotFoundException)
        {
            await HistoryDialogs.ShowMessageAsync(XamlRoot, "The version could not be deleted", ex.Message);
        }
        await ReloadAsync(ViewModel.ViewedVersionId == versionId ? null : ViewModel.ViewedVersionId);
    }

    // ── Diff ──────────────────────────────────────────────────────────────

    private async void DiffPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressDiffPicker && ViewModel.IsDiffView)
            await RenderDiffAsync();
    }

    private void ChangesOnly_Toggled(object sender, RoutedEventArgs e) => RenderDiffList();

    private void CloseDiff_Click(object sender, RoutedEventArgs e) => ViewModel.IsDiffMode = false;

    private async Task RenderDiffAsync()
    {
        if (DiffFrom.SelectedItem is not VersionChoice a || DiffTo.SelectedItem is not VersionChoice b)
        {
            DiffStats.Text = "Pick two versions";
            DiffList.Children.Clear();
            return;
        }
        DiffStats.Text = "Comparing…";
        _diff = await ViewModel.BuildDiffAsync(a.Id, b.Id);
        DiffStats.Text = _diff?.StatsText ?? "The versions could not be compared.";
        RenderDiffList();
    }

    private void RenderDiffList()
    {
        DiffList.Children.Clear();
        if (_diff is null)
            return;
        var segments = ChangesOnlySwitch.IsOn ? _diff.ChangedSegments : _diff.Segments;
        if (segments.Count == 0)
        {
            DiffList.Children.Add(new TextBlock
            {
                Text = _diff.IsIdentical ? "The two versions have the same words." : "No segments to show.",
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
            return;
        }

        var dark = ActualTheme == ElementTheme.Dark;
        var deleted = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4) : Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
        var inserted = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F) : Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F));

        foreach (var segment in segments)
        {
            var text = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
            var first = true;
            foreach (var run in segment.Runs)
            {
                if (!first)
                    text.Inlines.Add(new Run { Text = " " });
                first = false;
                var r = new Run { Text = run.Text };
                if (run.Kind == HistoryDiffRunKind.Deleted)
                {
                    r.Foreground = deleted;
                    r.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                }
                else if (run.Kind == HistoryDiffRunKind.Inserted)
                {
                    r.Foreground = inserted;
                    r.FontWeight = FontWeights.SemiBold;
                    r.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                }
                text.Inlines.Add(r);
            }

            var time = new TextBlock { Text = segment.TimeText, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 3, 0, 0) };
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(time);
            grid.Children.Add(text);

            var button = new Button
            {
                Content = grid,
                Style = (Style)Resources["HistoryRowButtonStyle"],
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(12, 10, 12, 10),
            };
            AutomationProperties.SetName(button, segment.TimeText + " " + string.Join(" ", segment.Runs.Select(r => r.Text)));
            var start = segment.Start;
            button.Click += (_, _) => Player.Seek(TimeSpan.FromSeconds(start));
            DiffList.Children.Add(button);
        }
    }

    // ── Back ──────────────────────────────────────────────────────────────

    private async Task GoBackAsync()
    {
        if (ViewModel.IsEditing)
        {
            await CancelEditAsync();
            return;
        }
        if (ViewModel.IsDiffView)
        {
            ViewModel.IsDiffMode = false;
            return;
        }
        if (!_isEmbedded && Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await GoBackAsync();

    private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.GoBack || (e.Key == VirtualKey.Escape && e.OriginalSource is not TextBox))
        {
            e.Handled = true;
            await GoBackAsync();
        }
    }

    private void SubscribeSystemBack()
    {
#if !VOICE_TYPE_WINDOWS
        if (_systemNavigation is not null)
            return;
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
    private async void OnSystemBackRequested(object? sender, Windows.UI.Core.BackRequestedEventArgs e)
    {
        if (e.Handled)
            return;
        e.Handled = true;
        await GoBackAsync();
    }
#endif
}
