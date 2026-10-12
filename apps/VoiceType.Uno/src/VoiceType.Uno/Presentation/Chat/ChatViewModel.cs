using System.Collections.ObjectModel;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>Layout numbers the message templates bind to (set by the page on resize).</summary>
public sealed class ChatLayoutState : ObservableObject
{
    private double _bubbleMaxWidth = 520;

    public double BubbleMaxWidth
    {
        get => _bubbleMaxWidth;
        set => SetProperty(ref _bubbleMaxWidth, value);
    }
}

/// <summary>One bubble, answer or event line of the thread; the streaming answer is one of these growing.</summary>
public sealed class ChatMessageViewModel : ObservableObject
{
    private string _text;
    private bool _isStreaming;
    private string _statusText = "";
    private bool _isIncomplete;
    private string _incompleteText = "";
    private IReadOnlyList<ChatCitationChip> _citations;

    public ChatMessageViewModel(ChatLayoutState layout, ChatThreadItem item, string incompleteText = "")
    {
        Layout = layout;
        Id = item.Id;
        Kind = item.Kind;
        _text = item.Text;
        TimeText = item.TimeText;
        ShowRoleLabel = item.ShowRoleLabel;
        ShowTime = item.ShowTime;
        _isIncomplete = item.IsIncomplete;
        _incompleteText = incompleteText;
        _citations = item.Citations;
    }

    public ChatLayoutState Layout { get; }
    public string Id { get; }
    public ChatThreadKind Kind { get; }
    public string TimeText { get; private set; }
    public bool ShowRoleLabel { get; }
    public bool ShowTime { get; }

    public bool IsUser => Kind == ChatThreadKind.User;
    public bool IsAssistant => Kind == ChatThreadKind.Assistant;
    public bool IsEvent => Kind == ChatThreadKind.Event;
    public string RoleLabel => IsUser ? "You" : "✦ Gemma";
    public bool ShowRole => ShowRoleLabel && !IsEvent;
    public bool HasTime => ShowTime && TimeText.Length > 0;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(CanCopy));
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                OnPropertyChanged(nameof(CanCopy));
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(ShowFooter));
            }
        }
    }

    /// <summary>Status line of the running answer ("Reading 3 recordings…").</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _statusText.Length > 0 && _isStreaming;

    /// <summary>Spinner next to the status while nothing was streamed yet.</summary>
    public bool ShowProgress => _isStreaming && _text.Length == 0;

    public bool IsIncomplete
    {
        get => _isIncomplete;
        private set => SetProperty(ref _isIncomplete, value);
    }

    public string IncompleteText => _incompleteText;

    public IReadOnlyList<ChatCitationChip> Citations
    {
        get => _citations;
        set
        {
            if (SetProperty(ref _citations, value))
                OnPropertyChanged(nameof(HasCitations));
        }
    }

    public bool HasCitations => _citations.Count > 0;

    public bool CanCopy => IsAssistant && !_isStreaming && _text.Length > 0;

    /// <summary>Copy button and time row of an answer.</summary>
    public bool ShowFooter => IsAssistant && !_isStreaming;

    public string AutomationName => (IsUser ? "You: " : IsAssistant ? "Gemma: " : "") + _text;

    /// <summary>Marks the streamed answer as stopped (the page keeps the partial text).</summary>
    public void MarkIncomplete(string text)
    {
        _incompleteText = text;
        OnPropertyChanged(nameof(IncompleteText));
        IsIncomplete = true;
    }

    /// <summary>The finished answer replaces the streamed text and gets its citations.</summary>
    public void Complete(ChatMessage message, IReadOnlyList<ChatCitationChip> citations)
    {
        Text = message.Text.Trim();
        Citations = citations;
        TimeText = message.CreatedAt == default ? DateTimeOffset.Now.ToString("HH:mm") : message.CreatedAt.ToLocalTime().ToString("HH:mm");
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(HasTime));
        StatusText = "";
        IsStreaming = false;
    }
}

public sealed class SourceChipViewModel
{
    public SourceChipViewModel(string recordingId, string title, string versionId, bool exists, bool isOutdated, string? currentVersion)
    {
        RecordingId = recordingId;
        Title = title;
        VersionId = versionId;
        Exists = exists;
        IsOutdated = isOutdated;
        CurrentVersion = currentVersion;
    }

    public string RecordingId { get; }
    public string Title { get; }
    public string VersionId { get; }
    public bool Exists { get; }
    public bool IsOutdated { get; }
    public string? CurrentVersion { get; }
    public string Label => ChatUiFormat.Shorten(Title, 26) + " " + VersionId;
    public string AutomationName => $"{Title}, version {VersionId}" + (IsOutdated ? $", outdated, current is {CurrentVersion}" : "") + ". Actions";
}

public sealed class QuickPromptViewModel
{
    public QuickPromptViewModel(QuickPrompt prompt) => Prompt = prompt;
    public QuickPrompt Prompt { get; }
    public string Label => Prompt.Label;
    public string AutomationName => "Ask: " + Prompt.Label;
}

/// <summary>
/// One open chat: thread, sources, the running answer and the model state. Reads and writes go
/// through <see cref="IChatService"/>; everything here runs on the UI thread, store reads on the pool.
/// </summary>
public sealed class ChatViewModel : ObservableObject
{
    private readonly ChatStore _store;
    private readonly IChatService? _service;
    private CancellationTokenSource? _cts;
    private ChatRecord? _chat;
    private string _title = "";
    private string _headerSubtitle = "";
    private bool _isLoaded;
    private bool _isStreaming;
    private string _error = "";
    private string? _lastQuestion;
    private bool _modelMissing;
    private string _noticeText = "";
    private string _noticeAction = "";
    private string? _noticeRecordingId;
    private string _sourcesNames = "";
    private string _contextLine = "";

    public ChatViewModel(ChatStore store, IChatService? service)
    {
        _store = store;
        _service = service;
        if (service is not null)
        {
            var prompts = service.QuickPrompts.ToList();
            // "Who said what" needs a speaker layer: offered where the speaker service exists (design §10.8).
            if (VoiceType.Uno.Presentation.History.SpeakerServices.Current is not null && prompts.TrueForAll(p => p.Id != "who-said-what"))
                prompts.Add(new QuickPrompt("who-said-what", SpeakersModelsUi.QuickPrompt, SpeakersModelsUi.QuickPromptInstruction));
            QuickPrompts = [.. prompts.Select(p => new QuickPromptViewModel(p))];
        }
        RefreshModelState();
    }

    public ChatLayoutState Layout { get; } = new();
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<SourceChipViewModel> Sources { get; } = [];
    public IReadOnlyList<QuickPromptViewModel> QuickPrompts { get; } = [];

    public string ChatId { get; private set; } = "";
    public ChatRecord? Chat => _chat;

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => SetProperty(ref _isLoaded, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string HeaderSubtitle
    {
        get => _headerSubtitle;
        private set => SetProperty(ref _headerSubtitle, value);
    }

    /// <summary><c>Weekly sync v2, Supplier call v1, +1</c> for the compact sources row.</summary>
    public string SourcesNames
    {
        get => _sourcesNames;
        private set
        {
            if (SetProperty(ref _sourcesNames, value))
                OnPropertyChanged(nameof(SourcesDetail));
        }
    }

    public string SourcesSummary => ChatUiFormat.SourcesSummary(Sources.Count);

    public string ContextLine
    {
        get => _contextLine;
        private set => SetProperty(ref _contextLine, value);
    }

    public bool HasSources => Sources.Count > 0;
    public bool NoSources => IsLoaded && Sources.Count == 0;

    public bool IsEmptyThread => IsLoaded && Messages.Count == 0;
    public bool HasMessages => Messages.Count > 0;

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (SetProperty(ref _isStreaming, value))
                RaiseComposerState();
        }
    }

    // ── Model ─────────────────────────────────────────────────────────

    public bool HasService => _service is not null;

    public bool ModelMissing
    {
        get => _modelMissing;
        private set
        {
            if (SetProperty(ref _modelMissing, value))
                RaiseComposerState();
        }
    }

    private string _modelBannerTitle = "";
    private string _modelBannerDetail = "";
    private bool _canDownload = true;

    /// <summary>"Gemma is not installed" and the download state under it (set by the page).</summary>
    public string ModelBannerTitle
    {
        get => _modelBannerTitle;
        set => SetProperty(ref _modelBannerTitle, value);
    }

    public string ModelBannerDetail
    {
        get => _modelBannerDetail;
        set => SetProperty(ref _modelBannerDetail, value);
    }

    /// <summary>The Download button is hidden while the download runs.</summary>
    public bool CanDownload
    {
        get => _canDownload;
        set => SetProperty(ref _canDownload, value);
    }

    /// <summary>Re-reads whether the Gemma model is installed (after a download finished).</summary>
    public void RefreshModelState() => ModelMissing = _service is null || !_service.IsAvailable;

    public bool CanType => HasService && !ModelMissing && HasSources;

    public bool CanSend => CanType && !IsStreaming;

    public string ComposerPlaceholder => !HasService ? "Chat is not available in this build"
        : ModelMissing ? "Download Gemma to ask questions"
        : !HasSources ? "Add a recording to continue"
        : "Ask about the recordings";

    private void RaiseComposerState()
    {
        OnPropertyChanged(nameof(CanType));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(ShowQuickPrompts));
    }

    // ── Error ─────────────────────────────────────────────────────────

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;
    public bool CanRetry => _lastQuestion is not null;

    /// <summary>The one-tap questions: shown in the empty chat when the model is ready.</summary>
    public bool ShowQuickPrompts => IsEmptyThread && CanType && !IsStreaming && QuickPrompts.Count > 0;

    // ── Outdated sources ──────────────────────────────────────────────

    public string NoticeText
    {
        get => _noticeText;
        private set
        {
            if (SetProperty(ref _noticeText, value))
            {
                OnPropertyChanged(nameof(HasNotice));
                OnPropertyChanged(nameof(ShowNotice));
                OnPropertyChanged(nameof(SourcesDetail));
            }
        }
    }

    public bool HasNotice => _noticeText.Length > 0;

    private bool _isShortLayout;

    /// <summary>Low window or open keyboard: the outdated-source notice shrinks to a mark on the sources row.</summary>
    public bool IsShortLayout
    {
        get => _isShortLayout;
        set
        {
            if (SetProperty(ref _isShortLayout, value))
            {
                OnPropertyChanged(nameof(ShowNotice));
                OnPropertyChanged(nameof(SourcesDetail));
            }
        }
    }

    public bool ShowNotice => HasNotice && !_isShortLayout;

    /// <summary>Source names for the compact row; a low window adds the outdated mark the notice would show.</summary>
    public string SourcesDetail => HasNotice && _isShortLayout ? "⚠ outdated · " + _sourcesNames : _sourcesNames;
    public string NoticeAction => _noticeAction;

    /// <summary>The source the notice's action updates; null opens the Sources sheet.</summary>
    public string? NoticeRecordingId => _noticeRecordingId;

    // ── Load ──────────────────────────────────────────────────────────

    public async Task LoadAsync(string chatId)
    {
        ChatId = chatId;
        var loaded = await Task.Run(() => Read(chatId));
        Apply(loaded);
    }

    /// <summary>Reloads from the store (after sources changed or a turn finished).</summary>
    public async Task ReloadAsync()
    {
        if (ChatId.Length == 0)
            return;
        var loaded = await Task.Run(() => Read(ChatId));
        Apply(loaded);
    }

    private sealed record Loaded(
        ChatRecord? Chat,
        IReadOnlyList<ChatThreadItem> Items,
        IReadOnlyList<SourceChipViewModel> Sources,
        IReadOnlyList<ChatOutdatedNotice> Notices,
        string Context);

    private Loaded Read(string chatId)
    {
        var chat = _store.Get(chatId);
        if (chat is null)
            return new Loaded(null, [], [], [], "");

        var titles = new Dictionary<string, string?>(StringComparer.Ordinal);
        var versions = new Dictionary<string, string?>(StringComparer.Ordinal);
        var durations = new Dictionary<string, double>(StringComparer.Ordinal);
        string? TitleOf(string id)
        {
            if (!titles.TryGetValue(id, out var t))
            {
                var manifest = ChatServicesStore.Recordings.Get(id)?.Manifest;
                titles[id] = t = manifest?.EffectiveTitle;
                versions[id] = manifest?.CurrentVersion;
                if (manifest is not null)
                    durations[id] = HistoryUiFormat.DurationSeconds(manifest);
            }
            return t;
        }
        string? CurrentOf(string id)
        {
            _ = TitleOf(id);
            return versions.GetValueOrDefault(id);
        }

        var items = ChatUiThread.Build(chat, TitleOf, VoiceType.Uno.Presentation.History.SpeakerCitations.NameOf);
        var sources = chat.Sources.Select(s =>
        {
            var title = TitleOf(s.RecordingId);
            var current = CurrentOf(s.RecordingId);
            return new SourceChipViewModel(s.RecordingId, title ?? "Deleted recording", s.VersionId, title is not null,
                current is not null && current != s.VersionId, current);
        }).ToList();
        var notices = ChatUiThread.OutdatedNotices(chat, CurrentOf, TitleOf);
        var context = ChatUiFormat.ContextLine([.. chat.Sources.Select(s => (s.VersionId, durations.GetValueOrDefault(s.RecordingId)))]);
        return new Loaded(chat, items, sources, notices, context);
    }

    private void Apply(Loaded loaded)
    {
        _chat = loaded.Chat;
        Messages.Clear();
        Sources.Clear();
        if (loaded.Chat is null)
        {
            IsLoaded = true;
            Title = "Chat not found";
            HeaderSubtitle = "";
            SourcesNames = "";
            ContextLine = "";
            NoticeText = "";
            RaiseThreadState();
            return;
        }

        Title = ChatUiFormat.DisplayTitle(loaded.Chat);
        HeaderSubtitle = ChatUiFormat.HeaderSubtitle(loaded.Chat);
        foreach (var chip in loaded.Sources)
            Sources.Add(chip);
        SourcesNames = ChatUiFormat.SourcesLine([.. loaded.Sources.Select(s => (s.Title, s.VersionId))]);
        ContextLine = loaded.Context;

        var messageSource = loaded.Chat.Messages;
        for (var i = 0; i < loaded.Items.Count; i++)
        {
            var item = loaded.Items[i];
            Messages.Add(new ChatMessageViewModel(Layout, item,
                item.IsIncomplete ? ChatUiFormat.IncompleteText(messageSource[i]) : ""));
        }

        // One outdated source: text with its own "Use vN"; several: one summary row opening the sheet.
        var notices = loaded.Notices;
        if (notices.Count == 1)
        {
            NoticeText = notices[0].Text;
            _noticeAction = "Use " + loaded.Sources.First(s => s.RecordingId == notices[0].RecordingId).CurrentVersion;
            _noticeRecordingId = notices[0].RecordingId;
        }
        else if (notices.Count > 1)
        {
            NoticeText = $"{notices.Count} sources are outdated: answers use older transcript versions";
            _noticeAction = "Review";
            _noticeRecordingId = null;
        }
        else
        {
            NoticeText = "";
            _noticeAction = "";
            _noticeRecordingId = null;
        }
        OnPropertyChanged(nameof(NoticeAction));
        OnPropertyChanged(nameof(NoticeRecordingId));

        IsLoaded = true;
        RaiseThreadState();
    }

    private void RaiseThreadState()
    {
        OnPropertyChanged(nameof(SourcesSummary));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(NoSources));
        OnPropertyChanged(nameof(IsEmptyThread));
        OnPropertyChanged(nameof(HasMessages));
        RaiseComposerState();
    }

    // ── Send ──────────────────────────────────────────────────────────

    /// <summary>Raised after a bubble was added or grew, so the page can keep the end in view.</summary>
    public event Action? ThreadChanged;

    /// <summary>Sends the question and streams the answer into a growing bubble.</summary>
    public async Task SendAsync(string text)
    {
        text = text.Trim();
        if (_service is null || text.Length == 0 || !CanSend)
            return;

        _lastQuestion = text;
        OnPropertyChanged(nameof(CanRetry));
        Error = "";
        var wasEmpty = Messages.Count == 0;
        Messages.Add(new ChatMessageViewModel(Layout, new ChatThreadItem(
            "u" + Guid.NewGuid().ToString("N"), ChatThreadKind.User, text, DateTimeOffset.Now.ToString("HH:mm"),
            ShowRoleLabel: wasEmpty || Messages[^1].Kind != ChatThreadKind.User, ShowTime: true, IsIncomplete: false, Citations: [])));
        var answer = new ChatMessageViewModel(Layout, new ChatThreadItem(
            "a" + Guid.NewGuid().ToString("N"), ChatThreadKind.Assistant, "", "", ShowRoleLabel: true, ShowTime: true,
            IsIncomplete: false, Citations: []))
        {
            IsStreaming = true,
            StatusText = "Thinking…",
        };
        Messages.Add(answer);
        RaiseThreadState();
        ThreadChanged?.Invoke();

        _cts = new CancellationTokenSource();
        IsStreaming = true;
        var failed = false;
        var completed = false;
        try
        {
            await foreach (var e in _service.SendAsync(ChatId, text, _cts.Token))
            {
                switch (e)
                {
                    case ChatEvent.Status status:
                        answer.StatusText = status.Text;
                        break;
                    case ChatEvent.Delta delta:
                        answer.Text += delta.Text;
                        ThreadChanged?.Invoke();
                        break;
                    case ChatEvent.Completed done:
                        completed = true;
                        answer.Complete(done.Message, ChatUiThread.Chips(done.Message.Citations, ChatServicesStore.TitleOf, speakerOf: VoiceType.Uno.Presentation.History.SpeakerCitations.NameOf));
                        break;
                    case ChatEvent.Failed fail:
                        failed = true;
                        Error = fail.Error;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop: the partial text stays as an incomplete answer.
        }
        catch (Exception ex)
        {
            failed = true;
            Error = ex.Message;
        }
        finally
        {
            var stopped = _cts?.IsCancellationRequested == true;
            _cts?.Dispose();
            _cts = null;
            answer.StatusText = "";
            answer.IsStreaming = false;
            if (failed && answer.Text.Length == 0)
            {
                Messages.Remove(answer);
            }
            else if (!completed && (stopped || failed))
            {
                answer.MarkIncomplete(failed ? "Answer incomplete · an error occurred" : "Stopped · answer incomplete");
            }
            IsStreaming = false;
        }

        // Pick up what the service saved: ids, source events, the generated title.
        if (!failed)
        {
            try
            {
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Chat reload failed: {ex.Message}");
            }
        }
        RaiseThreadState();
        ThreadChanged?.Invoke();
    }

    /// <summary>Re-sends the last question after an error.</summary>
    public Task RetryAsync()
    {
        var question = _lastQuestion;
        Error = "";
        return question is null ? Task.CompletedTask : SendAsync(question);
    }

    public void DismissError() => Error = "";

    /// <summary>Stop: cancels the running answer (about 40 ms natively); the partial text is kept.</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>The page was closed: stop the answer and free the live conversation.</summary>
    public void Close()
    {
        _cts?.Cancel();
        if (ChatId.Length > 0)
            _service?.CloseChat(ChatId);
    }
}

/// <summary>Store access for the chat view models (the stores are DI singletons).</summary>
internal static class ChatServicesStore
{
    public static RecordingStore Recordings => History.HistoryServices.Recordings;

    public static string? TitleOf(string recordingId) => ChatServices.TitleOf(recordingId);
}
