using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>
/// One chat (design §4.4). Opened with <c>Frame.Navigate(typeof(ChatPage), chatId)</c>, full screen or
/// inside <see cref="ChatsPage"/>'s detail pane (<see cref="IsEmbedded"/>, from 840 px).
/// </summary>
public sealed partial class ChatPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _reloadTimer;
    private MainViewModel? _main;
    private bool _isEmbedded;
    private bool _keyboardOpen;
    private bool _stickToEnd = true;
    private bool _hooksAttached;
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public ChatPage()
    {
        ViewModel = new ChatViewModel(HistoryServices.Chats, ChatServices.Current);
        InitializeComponent();
        ViewModel.ThreadChanged += OnThreadChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;

        _reloadTimer = _dispatcher.CreateTimer();
        _reloadTimer.Interval = TimeSpan.FromMilliseconds(400);
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += async (_, _) =>
        {
            if (!ViewModel.IsStreaming && ViewModel.ChatId.Length > 0)
                await ReloadAsync();
        };
    }

    public ChatViewModel ViewModel { get; }

    public string ChatId => ViewModel.ChatId;

    /// <summary>Hosted in the Chats detail pane: no back button, no system-back handling.</summary>
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

    /// <summary>The embedded page asks its host to close it (chat deleted).</summary>
    public event EventHandler? CloseRequested;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        HistoryServices.Chats.Changed += OnStoreChanged;
        HistoryServices.Recordings.Changed += OnRecordingsChanged;
        HistoryServices.Deletes.Changed += OnDeletesChanged;
        AttachMain();
        if (e.Parameter is string id && id.Length > 0)
            await LoadAsync(id);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Release();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isEmbedded)
            SubscribeSystemBack();
        AttachPlatformHooks();
        ApplyLayout();
        UpdateSendButton();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Release();

    private void Release()
    {
        HistoryServices.Chats.Changed -= OnStoreChanged;
        HistoryServices.Recordings.Changed -= OnRecordingsChanged;
        HistoryServices.Deletes.Changed -= OnDeletesChanged;
        DetachMain();
        UnsubscribeSystemBack();
        _reloadTimer.Stop();
        // Frees the engine conversation of this chat; a running answer is stopped.
        ViewModel.Close();
    }

    private async Task LoadAsync(string id)
    {
        await ViewModel.LoadAsync(id);
        ApplyEmptyTexts();
        _stickToEnd = true;
        ScrollToEnd();
    }

    private async Task ReloadAsync()
    {
        await ViewModel.ReloadAsync();
        ApplyEmptyTexts();
        if (_stickToEnd)
            ScrollToEnd();
    }

    private void ApplyEmptyTexts()
    {
        var many = ViewModel.Sources.Count > 1;
        EmptyTitle.Text = many ? "Ask Gemma about these recordings" : "Ask Gemma about this recording";
    }

    // Store events arrive on any thread; our own turn saves messages, so only idle chats reload.
    private void OnStoreChanged(string id)
    {
        if (id != ViewModel.ChatId || ViewModel.IsStreaming)
            return;
        _dispatcher.TryEnqueue(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    // A recording was renamed, re-recognized or deleted: titles, outdated marks and sources change.
    private void OnRecordingsChanged(string _)
    {
        if (ViewModel.ChatId.Length == 0 || ViewModel.IsStreaming)
            return;
        _dispatcher.TryEnqueue(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    private void OnDeletesChanged()
    {
        if (ViewModel.ChatId.Length > 0 && ChatDialogs.IsDeletePending(ViewModel.ChatId))
            Leave();
    }

    // ── Model download (reuses the translation-model download path) ─────────

    private void AttachMain()
    {
        try
        {
            _main = App.Services.GetService<MainViewModel>();
        }
        catch (InvalidOperationException)
        {
            _main = null;
        }
        if (_main is null)
            return;
        _main.PropertyChanged += OnMainChanged;
        UpdateModelBanner();
    }

    private void DetachMain()
    {
        if (_main is not null)
            _main.PropertyChanged -= OnMainChanged;
        _main = null;
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsTranslationModelMissing)
            or nameof(MainViewModel.IsQueueProgressVisible) or nameof(MainViewModel.QueueProgressText))
            _dispatcher.TryEnqueue(UpdateModelBanner);
    }

    private void UpdateModelBanner()
    {
        ViewModel.RefreshModelState();
        if (!ViewModel.ModelMissing)
            return;
        var downloading = _main?.IsQueueProgressVisible == true;
        ViewModel.CanDownload = !downloading && _main is not null;
        var llm = App.Services.GetService<VoiceType.Uno.Services.ILlmModelService>();
        var model = llm?.Active ?? VoiceType.Uno.Services.LlmModelCatalog.Default;
        ViewModel.ModelBannerTitle = VoiceType.Uno.Services.LlmUiText.ChatMissingTitle(
            llm is null ? "Gemma" : model.Title, downloading);
        ViewModel.ModelBannerDetail = downloading
            ? _main?.QueueProgressText ?? ""
            : VoiceType.Uno.Services.LlmUiText.ChatMissingDetail(model);
    }

    private void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        _main?.EnqueueTranslationModelDownload();
        UpdateModelBanner();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsStreaming) or nameof(ChatViewModel.CanSend) or nameof(ChatViewModel.CanType))
            UpdateSendButton();
    }

    // ── Layout ────────────────────────────────────────────────────────────

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0)
            return;

        var compactHeader = _keyboardOpen || ChatUiLayout.IsShort(height);
        Header.MinHeight = compactHeader ? 48 : 56;
        Header.Padding = compactHeader ? new Thickness(4, 0, 4, 0) : new Thickness(4);

        // Sources: chips from 600x480; otherwise one summary row; hidden while the keyboard is open.
        var chips = ChatUiLayout.UseSourceChips(width, height);
        SourcesPane.Visibility = _keyboardOpen ? Visibility.Collapsed : Visibility.Visible;
        ChipsPanel.Visibility = chips ? Visibility.Visible : Visibility.Collapsed;
        AddChip.Visibility = chips ? Visibility.Visible : Visibility.Collapsed;
        ContextText.Visibility = chips ? Visibility.Visible : Visibility.Collapsed;
        SummaryRow.Visibility = chips ? Visibility.Collapsed : Visibility.Visible;

        ViewModel.IsShortLayout = compactHeader;
        ViewModel.Layout.BubbleMaxWidth = Math.Clamp(width - 32 - 24, 200, 640);
    }

    // ── Platform hooks: on-screen keyboard and system back ────────────────

    private void AttachPlatformHooks()
    {
        if (_hooksAttached)
            return;
        _hooksAttached = true;
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
#endif
    }

    private void SetKeyboardOpen(bool isOpen)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_keyboardOpen == isOpen)
                return;
            _keyboardOpen = isOpen;
            ApplyLayout();
            if (isOpen)
                ScrollToEnd();
        });
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

    private void Back_Click(object sender, RoutedEventArgs e) => Leave();

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.GoBack || (e.Key == VirtualKey.Escape && e.OriginalSource is not TextBox && !_isEmbedded))
        {
            e.Handled = true;
            Leave();
        }
    }

    /// <summary>Back (or the chat was deleted): out of the page, or ask the host to close the pane.</summary>
    private void Leave()
    {
        if (_isEmbedded)
            CloseRequested?.Invoke(this, EventArgs.Empty);
        else if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    // ── Thread ────────────────────────────────────────────────────────────

    private void OnThreadChanged()
    {
        // Only follow the answer while the reader is at the end of the thread.
        var nearEnd = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset < 120;
        if (nearEnd || ViewModel.IsStreaming && _stickToEnd)
            ScrollToEnd();
        _stickToEnd = nearEnd;
    }

    private void ScrollToEnd()
    {
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            MessagesScroll.UpdateLayout();
            MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private void Citation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatCitationChip chip && chip.RecordingExists)
            ChatServices.OpenRecording(XamlRoot, new RecordingTarget(chip.RecordingId, chip.StartSec, chip.VersionId));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessageViewModel message || !message.CanCopy)
            return;
        var package = new DataPackage();
        package.SetText(message.Text);
        Clipboard.SetContent(package);
    }

    // ── Composer ──────────────────────────────────────────────────────────

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsStreaming)
        {
            ViewModel.Stop();
            return;
        }
        await SendCurrentAsync();
    }

    private async Task SendCurrentAsync()
    {
        var text = MessageBox.Text.Trim();
        if (text.Length == 0 || !ViewModel.CanSend)
            return;
        MessageBox.Text = "";
        _stickToEnd = true;
        await ViewModel.SendAsync(text);
    }

    private async void QuickPrompt_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuickPromptViewModel prompt)
        {
            _stickToEnd = true;
            await ViewModel.SendAsync(prompt.Prompt.Prompt);
        }
    }

    private void Prompts_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var prompt in ViewModel.QuickPrompts)
        {
            var item = new MenuFlyoutItem { Text = prompt.Label, MinHeight = 44 };
            var p = prompt;
            item.Click += async (_, _) => await ViewModel.SendAsync(p.Prompt.Prompt);
            menu.Items.Add(item);
        }
        menu.ShowAt((FrameworkElement)sender);
    }

    private bool _shiftDown;
    private bool _ctrlDown;

    private async void MessageBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        TrackModifiers(e.Key, down: true);
        if (e.Key != VirtualKey.Enter)
            return;
#if __ANDROID__
        // The soft keyboard's Enter is a newline; Ctrl+Enter (hardware keyboard) sends.
        var send = _ctrlDown;
#else
        var send = !_shiftDown;
#endif
        if (send)
        {
            e.Handled = true;
            await SendCurrentAsync();
        }
    }

    private void MessageBox_PreviewKeyUp(object sender, KeyRoutedEventArgs e) => TrackModifiers(e.Key, down: false);

    private void TrackModifiers(VirtualKey key, bool down)
    {
        if (key is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift)
            _shiftDown = down;
        else if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
            _ctrlDown = down;
    }

    private void MessageBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSendButton();

    private void MessageBox_GotFocus(object sender, RoutedEventArgs e)
    {
#if DEBUG
        // Layout check on a desktop: VOICETYPE_SIM_KEYBOARD=1 treats focus in the composer as an open keyboard.
        if (Environment.GetEnvironmentVariable("VOICETYPE_SIM_KEYBOARD") == "1")
            SetKeyboardOpen(true);
#endif
    }

    private void MessageBox_LostFocus(object sender, RoutedEventArgs e)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("VOICETYPE_SIM_KEYBOARD") == "1")
            SetKeyboardOpen(false);
#endif
    }

    /// <summary>Send ↔ Stop while an answer streams; Send is enabled when there is text to send.</summary>
    private void UpdateSendButton()
    {
        if (SendButton is null)
            return;
        if (ViewModel.IsStreaming)
        {
            SendIcon.Symbol = Symbol.Stop;
            SendButton.IsEnabled = true;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SendButton, "Stop");
            ToolTipService.SetToolTip(SendButton, "Stop");
        }
        else
        {
            SendIcon.Symbol = Symbol.Send;
            SendButton.IsEnabled = ViewModel.CanSend && MessageBox.Text.Trim().Length > 0;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SendButton, "Send");
            ToolTipService.SetToolTip(SendButton, "Send");
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await ViewModel.RetryAsync();

    private void DismissError_Click(object sender, RoutedEventArgs e) => ViewModel.DismissError();

    // ── Header actions ────────────────────────────────────────────────────

    private async void Rename_Click(object sender, RoutedEventArgs e) => await RenameAsync();

    private async Task RenameAsync()
    {
        if (ViewModel.Chat is not { } chat)
            return;
        var title = await ChatDialogs.RenameAsync(XamlRoot, chat);
        if (title is null)
            return;
        await Task.Run(() => HistoryServices.Chats.Rename(chat.Id, title));
        await ReloadAsync();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Rename", Symbol.Rename, RenameAsync));
        menu.Items.Add(Item("Sources…", Symbol.List, ShowSourcesAsync));
        menu.Items.Add(Item("Add recordings…", Symbol.Add, AddRecordingsAsync));
        var export = Item("Export as Markdown…", Symbol.Save, () => ChatDialogs.ExportAsync(XamlRoot, ViewModel.ChatId));
        export.IsEnabled = ViewModel.HasMessages;
        menu.Items.Add(export);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete", Symbol.Delete, DeleteAsync));
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
                Console.WriteLine($"[VoiceType.Uno] Chat action '{text}' failed: {ex}");
            }
        };
        return item;
    }

    private async Task DeleteAsync()
    {
        if (ViewModel.Chat is not { } chat)
            return;
        var outcome = await ChatDialogs.DeleteChatAsync(XamlRoot, chat);
        if (outcome != DeleteOutcome.Cancelled)
            Leave();
    }

    // ── Sources ───────────────────────────────────────────────────────────

    private async void Sources_Click(object sender, RoutedEventArgs e) => await ShowSourcesAsync();

    private async void AddRecordings_Click(object sender, RoutedEventArgs e) => await AddRecordingsAsync();

    private async Task ShowSourcesAsync()
    {
        if (ChatServices.Current is not { } service || ViewModel.ChatId.Length == 0)
            return;
        var outcome = await ChatDialogs.ShowSourcesAsync(XamlRoot, ViewModel.ChatId, service);
        await ReloadAsync();
        switch (outcome.Action)
        {
            case ChatDialogs.SourcesAction.Open when outcome.RecordingId is { } id:
                ChatServices.OpenRecording(XamlRoot, new RecordingTarget(id));
                break;
            case ChatDialogs.SourcesAction.Add:
                await AddRecordingsAsync();
                break;
        }
    }

    private async Task AddRecordingsAsync()
    {
        if (ChatServices.Current is not { } service || ViewModel.Chat is not { } chat)
            return;
        var already = chat.Sources.Select(s => s.RecordingId).ToHashSet(StringComparer.Ordinal);
        var picked = await ChatDialogs.PickRecordingsAsync(XamlRoot, "Add recordings", "Add {0}", already,
            "Already in this chat", "The first answer after adding prepares summaries of the new recordings.");
        if (picked is null || picked.Count == 0)
            return;
        try
        {
            await Task.Run(() =>
            {
                foreach (var id in picked)
                    service.AddSource(chat.Id, id);
            });
        }
        catch (Exception ex)
        {
            await HistoryDialogs.ShowMessageAsync(XamlRoot, "Could not add the recordings", ex.Message);
        }
        await ReloadAsync();
    }

    private void SourceChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SourceChipViewModel chip)
            return;
        var menu = new MenuFlyout();
        var open = Item("Open recording", Symbol.OpenFile,
            () => { ChatServices.OpenRecording(XamlRoot, new RecordingTarget(chip.RecordingId)); return Task.CompletedTask; });
        open.IsEnabled = chip.Exists;
        menu.Items.Add(open);
        if (chip.IsOutdated)
            menu.Items.Add(Item("Use current version " + chip.CurrentVersion, Symbol.Sync, () => UseCurrentAsync(chip.RecordingId)));
        var remove = Item("Remove from chat", Symbol.Delete, () => RemoveSourceAsync(chip.RecordingId));
        remove.IsEnabled = ViewModel.Sources.Count > 1;
        menu.Items.Add(remove);
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task UseCurrentAsync(string recordingId)
    {
        if (ChatServices.Current is not { } service)
            return;
        // Adding a source that is already in the chat moves it to the recording's current version.
        await Task.Run(() => service.AddSource(ViewModel.ChatId, recordingId));
        await ReloadAsync();
    }

    private async Task RemoveSourceAsync(string recordingId)
    {
        if (ChatServices.Current is not { } service)
            return;
        await Task.Run(() => service.RemoveSource(ViewModel.ChatId, recordingId));
        await ReloadAsync();
    }

    private async void Notice_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NoticeRecordingId is { } id)
            await UseCurrentAsync(id);
        else
            await ShowSourcesAsync();
    }
}
