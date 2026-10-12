using System.Collections.ObjectModel;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

public sealed class HistoryBadgeViewModel
{
    public HistoryBadgeViewModel(HistoryUiBadge badge)
    {
        Text = badge.Text;
        AutomationName = badge.AutomationName;
        IsAccent = badge.Kind == HistoryBadgeKind.Accent;
        IsWarning = badge.Kind == HistoryBadgeKind.Warning;
        IsNeutral = badge.Kind == HistoryBadgeKind.Neutral;
    }

    public string Text { get; }
    public string AutomationName { get; }
    public bool IsAccent { get; }
    public bool IsWarning { get; }
    public bool IsNeutral { get; }
}

/// <summary>One row of the History list.</summary>
public sealed class HistoryRowViewModel : ObservableObject
{
    private HistoryUiRow _row;
    private bool _isSelected;
    private bool _isSelectionMode;
    private bool _isCurrent;

    public HistoryRowViewModel(HistoryUiRow row)
    {
        _row = row;
        Badges = row.Badges.Select(b => new HistoryBadgeViewModel(b)).ToList();
    }

    public HistoryUiRow Row => _row;
    public string Id => _row.Id;
    public string Title => _row.Title;
    public string DurationText => _row.DurationText;
    public string MetaText => _row.MetaText;
    public string Snippet => _row.Snippet ?? "";
    public bool HasSnippet => _row.Snippet is not null;
    public bool HasBadges => Badges.Count > 0;
    public IReadOnlyList<HistoryBadgeViewModel> Badges { get; }

    public string AutomationName => IsSelectionMode
        ? (IsSelected ? "Selected: " : "Not selected: ") + _row.AutomationName
        : _row.AutomationName;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                RaiseLook();
        }
    }

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (SetProperty(ref _isSelectionMode, value))
                RaiseLook();
        }
    }

    /// <summary>Open in the detail pane (wide layout).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
                RaiseLook();
        }
    }

    public bool IsHighlighted => IsSelected || (IsCurrent && !IsSelectionMode);
    public bool ShowDuration => !IsSelectionMode;
    public bool ShowMore => !IsSelectionMode;
    public bool ShowCheckmark => IsSelectionMode && IsSelected;

    private void RaiseLook()
    {
        OnPropertyChanged(nameof(IsHighlighted));
        OnPropertyChanged(nameof(ShowDuration));
        OnPropertyChanged(nameof(ShowMore));
        OnPropertyChanged(nameof(ShowCheckmark));
        OnPropertyChanged(nameof(AutomationName));
    }
}

public sealed class HistoryGroupViewModel
{
    public HistoryGroupViewModel(string label, IReadOnlyList<HistoryRowViewModel> rows)
    {
        Label = label;
        Rows = rows;
    }

    public string Label { get; }
    public bool HasLabel => Label.Length > 0;
    public IReadOnlyList<HistoryRowViewModel> Rows { get; }
}

public sealed class HistoryChipViewModel
{
    public HistoryChipViewModel(HistoryUiFilterChip chip) => Chip = chip;
    public HistoryUiFilterChip Chip { get; }
    public string Label => Chip.Label;
    public string AutomationName => "Remove filter " + Chip.Label;
}

/// <summary>
/// History list: loads the recordings and their search documents off the UI thread,
/// filters/sorts them in memory as the user types, groups them by date and keeps the
/// multi-selection across reloads.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly RecordingStore _store;
    private readonly ChatStore _chats;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private Dictionary<string, RecordingManifest> _manifests = new(StringComparer.Ordinal);
    private Dictionary<string, long> _audioBytes = new(StringComparer.Ordinal);
    private IReadOnlyList<HistorySearchDocument> _documents = [];
    private HistoryStorageUsage _usage;
    private string _query = "";
    private HistoryUiFilterOptions _filter = HistoryUiFilterOptions.Default;
    private bool _isSelectionMode;
    private bool _isLoaded;
    private string? _currentId;
    private int _loadGeneration;

    public HistoryViewModel(RecordingStore store, ChatStore chats)
    {
        _store = store;
        _chats = chats;
    }

    public ObservableCollection<HistoryGroupViewModel> Groups { get; } = [];
    public ObservableCollection<HistoryChipViewModel> FilterChips { get; } = [];

    public IEnumerable<HistoryRowViewModel> Rows => Groups.SelectMany(g => g.Rows);

    public string Subtitle => HistoryUiFormat.ListSubtitle(VisibleTotal, _usage.TotalBytes);
    public string StorageTitle => HistoryUiStorage.CardTitle(_usage, HistoryServices.HistorySettings);
    public string StorageDetail => HistoryUiStorage.CardDetail(HistoryServices.HistorySettings);

    public bool IsFilterActive => _filter.IsActive;
    public bool HasChips => FilterChips.Count > 0;
    public bool IsEmpty => _isLoaded && !Groups.Any();
    public bool HasRows => Groups.Any();
    public bool ShowStorage => _isLoaded && VisibleTotal > 0;

    public string EmptyTitle => VisibleTotal == 0 ? "No recordings yet" : "Nothing found";
    public string EmptyText => VisibleTotal == 0
        ? "Press Record on the main screen. Every session is saved here with its audio and transcript, so you can listen, search, edit and export it later."
        : "No recording matches the search or the filters. Clear them to see everything.";

    public HistoryUiFilterOptions Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? HistoryUiFilterOptions.Default;
            FilterChips.Clear();
            foreach (var chip in _filter.Chips())
                FilterChips.Add(new HistoryChipViewModel(chip));
            OnPropertyChanged(nameof(IsFilterActive));
            OnPropertyChanged(nameof(HasChips));
            Apply();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? ""))
                Apply();
        }
    }

    // ── Selection ─────────────────────────────────────────────────────────

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (!SetProperty(ref _isSelectionMode, value))
                return;
            if (!value)
                _selected.Clear();
            foreach (var row in Rows)
            {
                row.IsSelectionMode = value;
                row.IsSelected = _selected.Contains(row.Id);
            }
            OnPropertyChanged(nameof(IsNormalMode));
            RaiseSelection();
        }
    }

    public bool IsNormalMode => !_isSelectionMode;

    public IReadOnlyList<string> SelectedIds => Rows.Where(r => _selected.Contains(r.Id)).Select(r => r.Id).ToList();

    public string SelectionTitle => HistoryUiFormat.Count(_selected.Count, "selected", "selected");

    public string SelectionSubtitle => HistoryUiRows.SelectionSubtitle(Rows.Where(r => r.IsSelected).Select(r => r.Row));

    public bool HasSelection => _selected.Count > 0;

    public void Toggle(HistoryRowViewModel row)
    {
        if (!_selected.Remove(row.Id))
            _selected.Add(row.Id);
        row.IsSelected = _selected.Contains(row.Id);
        if (_selected.Count == 0)
            IsSelectionMode = false;
        RaiseSelection();
    }

    public void StartSelection(HistoryRowViewModel row)
    {
        _selected.Add(row.Id);
        if (!IsSelectionMode)
            IsSelectionMode = true;
        row.IsSelected = true;
        RaiseSelection();
    }

    public void SelectAll()
    {
        var all = Rows.ToList();
        var allSelected = all.All(r => _selected.Contains(r.Id));
        _selected.Clear();
        if (!allSelected)
            foreach (var row in all)
                _selected.Add(row.Id);
        foreach (var row in all)
            row.IsSelected = _selected.Contains(row.Id);
        RaiseSelection();
    }

    public long SelectedAudioBytes => Rows.Where(r => r.IsSelected).Sum(r => r.Row.AudioBytes);

    public int ChatCountOf(IEnumerable<string> ids) =>
        _documents.Where(d => ids.Contains(d.RecordingId)).Sum(d => d.ChatCount);

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Marks the row shown in the detail pane.</summary>
    public string? CurrentId
    {
        get => _currentId;
        set
        {
            _currentId = value;
            foreach (var row in Rows)
                row.IsCurrent = row.Id == value;
        }
    }

    public RecordingManifest? ManifestOf(string id) => _manifests.GetValueOrDefault(id);

    // ── Loading ───────────────────────────────────────────────────────────

    /// <summary>Reloads the folders and the search index (thread pool), then re-applies the query.</summary>
    public async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        var data = await Task.Run(() =>
        {
            var chats = SafeChats();
            var manifests = new Dictionary<string, RecordingManifest>(StringComparer.Ordinal);
            var audio = new Dictionary<string, long>(StringComparer.Ordinal);
            var documents = new List<HistorySearchDocument>();
            foreach (var entry in _store.LoadAll().Recordings)
            {
                manifests[entry.Id] = entry.Manifest;
                audio[entry.Id] = RecordingStore.MeasureAudioBytes(entry.FolderPath);
                TranscriptDocument? transcript = null;
                var translations = new List<TranslationDocument>();
                try
                {
                    if (entry.Manifest.CurrentVersion is { } current)
                        transcript = _store.LoadTranscript(entry.Id, current);
                    foreach (var t in entry.Manifest.Translations)
                        translations.Add(_store.LoadTranslation(entry.Id, t.Id));
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
                {
                    // Index what loaded; a broken file must not hide the recording.
                }
                documents.Add(HistorySearch.CreateDocument(entry.Manifest, transcript, translations,
                    chats.Where(c => c.Includes(entry.Id))));
            }
            var usage = HistoryUiStorage.Measure(_store.SessionsDir, _chats.ChatsDir);
            return (manifests, audio, documents, usage);
        });

        if (generation != _loadGeneration)
            return; // a newer load is running
        _manifests = data.manifests;
        _audioBytes = data.audio;
        _documents = data.documents;
        _usage = data.usage;
        _isLoaded = true;
        _selected.IntersectWith(_manifests.Keys);
        Apply();
    }

    private IReadOnlyList<ChatRecord> SafeChats()
    {
        try
        {
            return _chats.LoadAll().Chats;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public int CountMatches(HistoryUiFilterOptions options) =>
        HistoryUiQuery.Run(VisibleDocuments, _manifests, _query, options).Count;

    public IReadOnlyList<HistoryUiChoice> LanguageChoices => HistoryUiQuery.LanguageChoices(VisibleDocuments);
    public IReadOnlyList<HistoryUiChoice> ModelChoices => HistoryUiQuery.ModelChoices(_manifests.Values);

    public int VisibleTotal => VisibleDocuments.Count();

    private IEnumerable<HistorySearchDocument> VisibleDocuments =>
        _documents.Where(d => !HistoryServices.Deletes.Pending.IsRecordingHidden(d.RecordingId));

    /// <summary>Re-runs search, filter, sort and grouping in memory (pending deletes are hidden).</summary>
    public void Apply()
    {
        var now = DateTimeOffset.Now;
        var hits = HistoryUiQuery.Run(VisibleDocuments, _manifests, _query, _filter);
        var rows = hits.Select(h => new HistoryRowViewModel(HistoryUiRows.Build(
            _manifests[h.Document.RecordingId], h.Document.ChatCount, now, h,
            _audioBytes.GetValueOrDefault(h.Document.RecordingId))))
            .ToList();

        foreach (var row in rows)
        {
            row.IsSelectionMode = _isSelectionMode;
            row.IsSelected = _selected.Contains(row.Id);
            row.IsCurrent = row.Id == _currentId;
        }

        var byDate = _filter.Sort == HistoryUiSort.NewestFirst;
        var rowById = rows.ToDictionary(r => r.Id);
        Groups.Clear();
        foreach (var group in HistoryUiRows.Group(rows.Select(r => r.Row), now, byDate))
            Groups.Add(new HistoryGroupViewModel(group.Label, group.Rows.Select(r => rowById[r.Id]).ToList()));

        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(StorageTitle));
        OnPropertyChanged(nameof(StorageDetail));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowStorage));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        RaiseSelection();
    }
}
