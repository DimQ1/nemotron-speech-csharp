using System.Collections.ObjectModel;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.Chat;

public sealed class ChatRowViewModel : ObservableObject
{
    private bool _isHighlighted;

    public ChatRowViewModel(ChatRecord chat, string meta)
    {
        Chat = chat;
        Id = chat.Id;
        Title = ChatUiFormat.DisplayTitle(chat);
        Meta = meta;
        LastLine = ChatUiFormat.LastLine(chat);
    }

    public ChatRecord Chat { get; }
    public string Id { get; }
    public string Title { get; }
    public string Meta { get; }
    public string LastLine { get; }
    public bool HasLastLine => LastLine.Length > 0;
    public string AutomationName => HistoryUiFormat.Join(Title, Meta, LastLine);

    /// <summary>The chat open in the detail pane (wide layout).</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }
}

/// <summary>Chats list: loads off the UI thread, filters in memory as the user types.</summary>
public sealed class ChatsViewModel : ObservableObject
{
    private readonly ChatStore _store;
    private IReadOnlyList<ChatRecord> _all = [];
    private string _query = "";
    private string _subtitle = ChatUiFormat.Subtitle;
    private string? _currentId;
    private bool _isLoaded;
    private Dictionary<string, string?> _titles = new(StringComparer.Ordinal);

    public ChatsViewModel(ChatStore store) => _store = store;

    public ObservableCollection<ChatRowViewModel> Rows { get; } = [];

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
                Apply();
        }
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => SetProperty(ref _isLoaded, value);
    }

    /// <summary>No chats at all (not just no search hits).</summary>
    public bool IsEmpty => IsLoaded && _all.Count == 0;

    public bool HasNoMatches => IsLoaded && _all.Count > 0 && Rows.Count == 0;

    public string? CurrentId
    {
        get => _currentId;
        set
        {
            if (SetProperty(ref _currentId, value))
                foreach (var row in Rows)
                    row.IsHighlighted = row.Id == value;
        }
    }

    public ChatRecord? Find(string id) => _all.FirstOrDefault(c => c.Id == id);

    public async Task LoadAsync()
    {
        var loaded = await Task.Run(() =>
        {
            var chats = _store.LoadAll().Chats;
            var titles = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var id in chats.SelectMany(c => c.Sources).Select(s => s.RecordingId).Distinct())
                titles[id] = ChatServices.TitleOf(id);
            return (chats, titles);
        });
        _all = loaded.chats;
        _titles = loaded.titles;
        IsLoaded = true;
        Apply();
    }

    /// <summary>Re-applies the search and the hidden (Undo-pending) deletes.</summary>
    public void Apply()
    {
        var now = DateTimeOffset.Now;
        var visible = _all.Where(c => !ChatDialogs.IsDeletePending(c.Id)).ToList();
        var filtered = ChatUiFormat.Filter(visible, _query, id => _titles.GetValueOrDefault(id));
        Rows.Clear();
        foreach (var chat in filtered)
        {
            Rows.Add(new ChatRowViewModel(chat, ChatUiFormat.RowMeta(chat, id => _titles.GetValueOrDefault(id), now))
            {
                IsHighlighted = chat.Id == _currentId,
            });
        }
        Subtitle = ChatUiFormat.ListSubtitle(visible.Count);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
    }
}
