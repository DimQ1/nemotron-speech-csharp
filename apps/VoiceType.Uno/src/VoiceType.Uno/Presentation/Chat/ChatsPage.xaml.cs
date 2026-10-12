using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;
using VirtualKey = Windows.System.VirtualKey;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>
/// Chats list (design §4.4). Opened with <c>Frame.Navigate(typeof(ChatsPage))</c>; an optional string
/// parameter (chat id) opens that chat at once. Below 840 px a row opens <see cref="ChatPage"/> full
/// screen; from 840 px it opens in the detail pane on the right.
/// </summary>
public sealed partial class ChatsPage : Page
{
    private const double ListWidth = 360;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly DispatcherQueueTimer _reloadTimer;
    private bool _isWide;
    private string? _openOnLoad;
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public ChatsPage()
    {
        ViewModel = new ChatsViewModel(HistoryServices.Chats);
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

    public ChatsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _openOnLoad = e.Parameter as string;
        HistoryServices.Chats.Changed += OnStoreChanged;
        HistoryServices.Recordings.Changed += OnStoreChanged;
        HistoryServices.Deletes.Changed += OnDeletesChanged;
        SubscribeSystemBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Release();
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

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Release();

    private void Release()
    {
        HistoryServices.Chats.Changed -= OnStoreChanged;
        HistoryServices.Recordings.Changed -= OnStoreChanged;
        HistoryServices.Deletes.Changed -= OnDeletesChanged;
        UnsubscribeSystemBack();
        _searchTimer.Stop();
        _reloadTimer.Stop();
    }

    // Store events arrive on any thread; coalesce bursts (a turn saves several times).
    private void OnStoreChanged(string _) => _dispatcher.TryEnqueue(() =>
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    private void OnDeletesChanged()
    {
        ViewModel.Apply();
        if (ViewModel.CurrentId is { } id && ChatDialogs.IsDeletePending(id))
            CloseDetail();
    }

    // ── Layout ────────────────────────────────────────────────────────────

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var wide = ChatUiLayout.IsMasterDetail(ActualWidth);
        if (wide == _isWide && ListColumn.Width.Value > 0)
            return;
        _isWide = wide;
        ListColumn.Width = wide ? new GridLength(ListWidth) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DetailPane.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        if (!wide)
            ViewModel.CurrentId = null;
        else if (DetailFrame.Content is ChatPage page)
            ViewModel.CurrentId = page.ChatId;
    }

    // ── Opening a chat ────────────────────────────────────────────────────

    private void Open(string id)
    {
        if (_isWide)
        {
            DetailFrame.Navigate(typeof(ChatPage), id);
            if (DetailFrame.Content is ChatPage page)
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
            Frame?.Navigate(typeof(ChatPage), id);
        }
    }

    private void OnDetailCloseRequested(object? sender, EventArgs e) => CloseDetail();

    private void CloseDetail()
    {
        if (DetailFrame.Content is ChatPage page)
        {
            page.CloseRequested -= OnDetailCloseRequested;
            // The page is dropped without a navigation event: free its engine conversation.
            page.ViewModel.Close();
        }
        DetailFrame.Content = null;
        DetailFrame.Visibility = Visibility.Collapsed;
        DetailPlaceholder.Visibility = Visibility.Visible;
        ViewModel.CurrentId = null;
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            Open(row.Id);
    }

    private static ChatRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ChatRowViewModel;

    // ── New chat ──────────────────────────────────────────────────────────

    private async void NewChat_Click(object sender, RoutedEventArgs e) => await NewChatAsync();

    private async Task NewChatAsync()
    {
        if (ChatServices.Current is not { } service)
        {
            await HistoryDialogs.ShowMessageAsync(XamlRoot, "Chat is not available", "This build has no chat service.");
            return;
        }
        var picked = await ChatDialogs.PickRecordingsAsync(XamlRoot, "New chat", "Start chat ({0})",
            new HashSet<string>(), "Already in this chat", "Pick the recordings Gemma should read. You can add or remove them later.");
        if (picked is null || picked.Count == 0)
            return;
        try
        {
            var chat = await Task.Run(() => service.CreateChat(picked));
            await ViewModel.LoadAsync();
            Open(chat.Id);
        }
        catch (Exception ex)
        {
            await HistoryDialogs.ShowMessageAsync(XamlRoot, "Could not start the chat", ex.Message);
        }
    }

    // ── Row ⋯ ─────────────────────────────────────────────────────────────

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || sender is not FrameworkElement anchor)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open", Symbol.OpenFile, () => { Open(row.Id); return Task.CompletedTask; }));
        menu.Items.Add(Item("Rename", Symbol.Rename, () => RenameAsync(row.Chat)));
        menu.Items.Add(Item("Export as Markdown…", Symbol.Save, () => ChatDialogs.ExportAsync(XamlRoot, row.Id)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete", Symbol.Delete, () => DeleteAsync(row.Chat)));
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
                Console.WriteLine($"[VoiceType.Uno] Chats action '{text}' failed: {ex}");
            }
        };
        return item;
    }

    private async Task RenameAsync(ChatRecord chat)
    {
        var title = await ChatDialogs.RenameAsync(XamlRoot, chat);
        if (title is null)
            return;
        await Task.Run(() => HistoryServices.Chats.Rename(chat.Id, title));
        await ViewModel.LoadAsync();
    }

    private async Task DeleteAsync(ChatRecord chat)
    {
        var outcome = await ChatDialogs.DeleteChatAsync(XamlRoot, chat);
        if (outcome == DeleteOutcome.Cancelled)
            return;
        if (ViewModel.CurrentId == chat.Id)
            CloseDetail();
        if (outcome == DeleteOutcome.Deleted)
            await ViewModel.LoadAsync();
        else
            ViewModel.Apply();
    }

    // ── Search ────────────────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    // ── Back ──────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

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
        if (Frame?.CanGoBack == true)
        {
            e.Handled = true;
            Frame.GoBack();
        }
    }
#endif
}
