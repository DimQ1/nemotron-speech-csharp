using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using VoiceType.Uno.History;
using VirtualKey = Windows.System.VirtualKey;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// History list (design §4.2). Opened with <c>Frame.Navigate(typeof(HistoryPage))</c>; an
/// optional string parameter (recording id) opens that recording at once. Below 840 px a
/// row opens <see cref="RecordingPage"/> full screen; from 840 px it opens in the detail
/// pane on the right.
/// </summary>
public sealed partial class HistoryPage : Page
{
    /// <summary>Width from which the list and the recording sit side by side.</summary>
    public const double MasterDetailWidth = 840;
    private const double ListWidth = 360;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly DispatcherQueueTimer _reloadTimer;
    private bool _isWide;
    private string? _openOnLoad;
    private (string Id, DateTimeOffset At) _lastHold;
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public HistoryPage()
    {
        ViewModel = new HistoryViewModel(HistoryServices.Recordings, HistoryServices.Chats);
        InitializeComponent();

        _searchTimer = _dispatcher.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => ViewModel.Query = SearchBox.Text;

        _reloadTimer = _dispatcher.CreateTimer();
        _reloadTimer.Interval = TimeSpan.FromMilliseconds(400);
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += async (_, _) => await ViewModel.LoadAsync();
    }

    public HistoryViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _openOnLoad = e.Parameter as string;
        HistoryServices.Recordings.Changed += OnStoreChanged;
        HistoryServices.Chats.Changed += OnStoreChanged;
        HistoryServices.Deletes.Changed += OnDeletesChanged;
        SubscribeSystemBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        HistoryServices.Recordings.Changed -= OnStoreChanged;
        HistoryServices.Chats.Changed -= OnStoreChanged;
        HistoryServices.Deletes.Changed -= OnDeletesChanged;
        UnsubscribeSystemBack();
        _searchTimer.Stop();
        _reloadTimer.Stop();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout();
        await ViewModel.LoadAsync();
        if (_openOnLoad is { } id)
        {
            _openOnLoad = null;
            Open(id);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        _reloadTimer.Stop();
    }

    // Store events arrive on any thread; coalesce bursts (a save touches several files).
    private void OnStoreChanged(string _) => _dispatcher.TryEnqueue(() =>
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    private void OnDeletesChanged()
    {
        ViewModel.Apply();
        if (ViewModel.CurrentId is { } id && HistoryServices.Deletes.Pending.IsRecordingHidden(id))
            CloseDetail();
    }

    // ── Layout ────────────────────────────────────────────────────────────

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var wide = ActualWidth >= MasterDetailWidth;
        if (wide == _isWide && ListColumn.Width.Value > 0)
            return;
        _isWide = wide;
        ListColumn.Width = wide ? new GridLength(ListWidth) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DetailPane.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        if (!wide)
            ViewModel.CurrentId = null;
        else if (DetailFrame.Content is RecordingPage page)
            ViewModel.CurrentId = page.RecordingId;
    }

    // ── Opening a recording ───────────────────────────────────────────────

    private void Open(string id)
    {
        if (_isWide)
        {
            DetailFrame.Navigate(typeof(RecordingPage), id);
            if (DetailFrame.Content is RecordingPage page)
            {
                page.IsEmbedded = true;
                page.CloseRequested += OnDetailCloseRequested;
            }
            DetailFrame.Visibility = Visibility.Visible;
            DetailPlaceholder.Visibility = Visibility.Collapsed;
            ViewModel.CurrentId = id;
        }
        else
        {
            Frame?.Navigate(typeof(RecordingPage), id);
        }
    }

    private void OnDetailCloseRequested(object? sender, EventArgs e) => CloseDetail();

    private void CloseDetail()
    {
        if (DetailFrame.Content is RecordingPage page)
            page.CloseRequested -= OnDetailCloseRequested;
        DetailFrame.Content = null;
        DetailFrame.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        ViewModel.CurrentId = null;
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
            return;
        // A long press already toggled this row; the release must not toggle it back.
        if (_lastHold.Id == row.Id && DateTimeOffset.Now - _lastHold.At < TimeSpan.FromMilliseconds(900))
            return;
        if (ViewModel.IsSelectionMode)
            ViewModel.Toggle(row);
        else
            Open(row.Id);
    }

    // Long press (touch: Holding; mouse and pen: right click) starts the selection.
    private void Row_Holding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == Microsoft.UI.Input.HoldingState.Started && RowOf(sender) is { } row)
        {
            e.Handled = true;
            BeginSelectionAt(row);
        }
    }

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
            return;
        e.Handled = true;
        if (_lastHold.Id == row.Id && DateTimeOffset.Now - _lastHold.At < TimeSpan.FromMilliseconds(900))
            return; // the Holding of the same press already handled it
        BeginSelectionAt(row);
    }

    private void BeginSelectionAt(HistoryRowViewModel row)
    {
        _lastHold = (row.Id, DateTimeOffset.Now);
        if (ViewModel.IsSelectionMode)
            ViewModel.Toggle(row);
        else
            ViewModel.StartSelection(row);
    }

    private static HistoryRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as HistoryRowViewModel;

    // ── Row ⋯ ─────────────────────────────────────────────────────────────

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || sender is not FrameworkElement anchor)
            return;

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Rename", Symbol.Rename, async () => await RenameAsync(row.Id)));
        menu.Items.Add(Item("Export…", Symbol.Save, async () => await ExportAsync(row.Id)));
        var reprocess = Item("Re-recognize…", Symbol.Refresh, async () => await ReprocessAsync([row.Id]));
        reprocess.IsEnabled = row.Row.HasAudio;
        menu.Items.Add(reprocess);
        menu.Items.Add(Item("Select", Symbol.SelectAll, () => { ViewModel.StartSelection(row); return Task.CompletedTask; }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete", Symbol.Delete, async () => await DeleteAsync([row.Id], row.Row.AudioBytes)));
        menu.ShowAt(anchor);
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
                Console.WriteLine($"[VoiceType.Uno] History action '{text}' failed: {ex}");
            }
        };
        return item;
    }

    private async Task RenameAsync(string id)
    {
        if (ViewModel.ManifestOf(id) is not { } manifest)
            return;
        var title = await HistoryDialogs.RenameAsync(XamlRoot, manifest);
        if (title is not null)
            await Task.Run(() => HistoryServices.Recordings.Rename(id, title));
    }

    private async Task ExportAsync(string id)
    {
        if (await ExportDialog.CreateAsync(id, null) is { } dialog)
            await dialog.RunAsync(XamlRoot);
    }

    private async Task ReprocessAsync(IReadOnlyList<string> ids) =>
        await HistoryDialogs.ShowSafeAsync(new ReprocessSheet(ids) { XamlRoot = XamlRoot });

    private async Task DeleteAsync(IReadOnlyList<string> ids, long audioBytes)
    {
        var outcome = await HistoryDialogs.DeleteRecordingsAsync(XamlRoot, ids, audioBytes, ViewModel.ChatCountOf(ids));
        if (outcome == DeleteOutcome.Cancelled)
            return;
        if (ViewModel.CurrentId is { } current && ids.Contains(current))
            CloseDetail();
        ViewModel.IsSelectionMode = false;
        if (outcome == DeleteOutcome.Deleted)
            await ViewModel.LoadAsync();
    }

    // ── Selection bar ─────────────────────────────────────────────────────

    private void CancelSelection_Click(object sender, RoutedEventArgs e) => ViewModel.IsSelectionMode = false;

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll();

    private async void ReprocessSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = ViewModel.SelectedIds;
        if (ids.Count > 0)
            await ReprocessAsync(ids);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = ViewModel.SelectedIds;
        if (ids.Count > 0)
            await DeleteAsync(ids, ViewModel.SelectedAudioBytes);
    }

    // ── Search and filter ─────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HistoryFilterDialog(ViewModel.Filter, ViewModel.LanguageChoices, ViewModel.ModelChoices,
            ViewModel.VisibleTotal, ViewModel.CountMatches) { XamlRoot = XamlRoot };
        if (await HistoryDialogs.ShowSafeAsync(dialog) == ContentDialogResult.Primary)
            ViewModel.Filter = dialog.Options;
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryChipViewModel chip)
            ViewModel.Filter = ViewModel.Filter.Without(chip.Chip.Key);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Frame?.Navigate(typeof(HistorySettingsPage));

    // ── Back ──────────────────────────────────────────────────────────────

    private void GoBack()
    {
        if (ViewModel.IsSelectionMode)
        {
            ViewModel.IsSelectionMode = false;
            return;
        }
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.GoBack || (e.Key == VirtualKey.Escape && e.OriginalSource is not TextBox))
        {
            e.Handled = true;
            GoBack();
        }
    }

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
        if (e.Handled)
            return;
        if (ViewModel.IsSelectionMode)
        {
            e.Handled = true;
            ViewModel.IsSelectionMode = false;
        }
        else if (Frame?.CanGoBack == true)
        {
            e.Handled = true;
            Frame.GoBack();
        }
    }
#endif
}
