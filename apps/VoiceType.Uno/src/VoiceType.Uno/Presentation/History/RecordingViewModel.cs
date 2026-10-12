using System.Collections.ObjectModel;
using System.Globalization;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

public enum RecordingTab
{
    Transcript,
    Chats,
    Translate,
    Versions,
}

/// <summary>One transcript segment (read and edit mode).</summary>
public sealed class SegmentViewModel : ObservableObject
{
    private bool _isCurrent;
    private string _editText;
    private bool _isChanged;

    public SegmentViewModel(int position, TranscriptSegment segment)
    {
        Position = position;
        Start = segment.Start;
        End = segment.End;
        Text = segment.Text.Trim();
        _editText = Text;
        TimeText = HistoryUiFormat.SegmentTime(segment.Start);
    }

    /// <summary>Position in the version's segment list (edit texts are passed in this order).</summary>
    public int Position { get; }
    public double Start { get; }
    public double End { get; }
    public string TimeText { get; }
    public string Text { get; }
    public string AutomationName => TimeText + " " + Text;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value ?? "");
    }

    public bool IsChanged
    {
        get => _isChanged;
        set => SetProperty(ref _isChanged, value);
    }
}

public sealed class ChatCardViewModel
{
    public string Id { get; init; } = "";
    public int SourceCount { get; init; }
    public string AutomationName => HistoryUiFormat.Join(Title, Meta, LastLine);
    public required string Title { get; init; }
    public required string Meta { get; init; }
    public required string LastLine { get; init; }
    public bool HasLastLine => LastLine.Length > 0;
}

/// <summary>One card of the Versions tab.</summary>
public sealed class VersionCardViewModel : ObservableObject
{
    private string _diffText = "";

    public VersionCardViewModel(TranscriptVersion version, RecordingManifest manifest, int chatCount, DateTimeOffset now)
    {
        Version = version;
        Id = version.Id;
        Title = HistoryUiFormat.VersionTitle(version);
        KindText = HistoryUiFormat.VersionKind(version);
        IsCurrent = manifest.CurrentVersion == version.Id;
        LanguageText = Empty(HistoryUiFormat.LanguageDetail(version.Language));
        VadText = Empty(HistoryUiFormat.Vad(version));
        CreatedText = HistoryUiFormat.ShortDateTime(version.CreatedAt, now);
        AppText = Empty(version.App?.Version);
        WordsText = (version.Stats?.Words ?? 0).ToString("N0", CultureInfo.InvariantCulture);

        var translations = manifest.Translations
            .Where(t => t.SourceVersion == version.Id && t.Status != TranslationStatuses.Failed)
            .Select(t => t.TargetLanguage.ToUpperInvariant())
            .Distinct()
            .ToList();
        var uses = new List<string>();
        if (translations.Count > 0)
            uses.Add((translations.Count == 1 ? "Translation " : "Translations ") + string.Join(", ", translations));
        if (chatCount > 0)
            uses.Add(HistoryUiFormat.Count(chatCount, "chat"));
        var plural = uses.Count == 2 || translations.Count > 1 || chatCount > 1;
        UsageText = uses.Count == 0 ? "" : string.Join(" and ", uses) + (plural ? " use" : " uses") + " this version";
    }

    public TranscriptVersion Version { get; }
    public string Id { get; }
    public string Title { get; }
    public string KindText { get; }
    public bool IsCurrent { get; }
    public bool IsNotCurrent => !IsCurrent;
    public string LanguageText { get; }
    public string VadText { get; }
    public string CreatedText { get; }
    public string AppText { get; }
    public string WordsText { get; }
    public string UsageText { get; }
    public bool HasUsage => UsageText.Length > 0;

    /// <summary><c>vs v1 3.4% diff</c>, filled in the background.</summary>
    public string DiffText
    {
        get => _diffText;
        set
        {
            if (SetProperty(ref _diffText, value))
                OnPropertyChanged(nameof(HasDiff));
        }
    }

    public bool HasDiff => DiffText.Length > 0;

    private static string Empty(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}

public sealed class VersionChoice
{
    public VersionChoice(TranscriptVersion version)
    {
        Id = version.Id;
        Label = HistoryUiFormat.VersionTiny(version);
    }

    public string Id { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

/// <summary>
/// Recording detail: header, tabs, the transcript of the viewed version, edit session,
/// chats that include the recording and the versions list. Store access runs on the
/// thread pool; properties change on the UI thread.
/// </summary>
public sealed class RecordingViewModel : ObservableObject
{
    private readonly RecordingStore _store;
    private readonly ChatStore _chats;
    private RecordingManifest? _manifest;
    private string? _viewedVersionId;
    private TranscriptDocument? _viewed;
    private RecordingTab _tab = RecordingTab.Transcript;
    private HistoryUiEditSession? _edit;
    private bool _isDiffMode;
    private string? _audioPath;
    private long _audioBytes;
    private int _chatCount;
    private IReadOnlyList<ChatRecord> _chatRecords = [];

    public RecordingViewModel(RecordingStore store, ChatStore chats)
    {
        _store = store;
        _chats = chats;
    }

    public string RecordingId { get; private set; } = "";

    public RecordingManifest? Manifest => _manifest;

    public bool IsMissing { get; private set; }

    public string Title => _manifest?.EffectiveTitle ?? (IsMissing ? "Recording not found" : "");

    public string Subtitle => _manifest is null ? "" : HistoryUiFormat.HeaderSubtitle(_manifest, DateTimeOffset.Now);

    public ObservableCollection<SegmentViewModel> Segments { get; } = [];
    public ObservableCollection<ChatCardViewModel> ChatCards { get; } = [];
    public ObservableCollection<VersionCardViewModel> Versions { get; } = [];
    public ObservableCollection<VersionChoice> VersionChoices { get; } = [];

    public string? AudioPath => _audioPath;
    public long AudioBytes => _audioBytes;
    public int ChatCount => _chatCount;
    public bool HasAudio => _audioPath is not null;
    public bool HasNoAudio => _manifest is not null && _audioPath is null;

    public string NoAudioText => _manifest?.Audio is { RemovedAt: not null } audio
        ? $"Audio removed on {audio.RemovedAt.Value.ToLocalTime():d MMM} · transcript only"
        : "No audio was saved for this recording · transcript only";

    public string? ViewedVersionId => _viewedVersionId;

    public string ViewedVersionLabel =>
        _manifest?.FindVersion(_viewedVersionId ?? "") is { } v
            ? HistoryUiFormat.VersionShort(v) + (v.Id == _manifest.CurrentVersion ? "" : " (not current)")
            : "No transcript";

    public TranscriptDocument? ViewedTranscript => _viewed;

    public bool HasSegments => Segments.Count > 0;
    public bool HasNoSegments => _manifest is not null && Segments.Count == 0;

    // ── Tabs ──────────────────────────────────────────────────────────────

    public RecordingTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value))
                return;
            if (value != RecordingTab.Versions)
                IsDiffMode = false;
            RaiseChrome();
        }
    }

    public bool IsTranscriptTab => _tab == RecordingTab.Transcript && !IsEditing;
    public bool IsChatsTab => _tab == RecordingTab.Chats && !IsEditing;
    public bool IsTranslateTab => _tab == RecordingTab.Translate && !IsEditing;
    public bool IsVersionsList => _tab == RecordingTab.Versions && !IsEditing && !_isDiffMode;
    public bool IsDiffView => _tab == RecordingTab.Versions && !IsEditing && _isDiffMode;

    public bool IsDiffMode
    {
        get => _isDiffMode;
        set
        {
            if (SetProperty(ref _isDiffMode, value))
                RaiseChrome();
        }
    }

    /// <summary>The player sits under Transcript and the diff (design §4.3), only with audio.</summary>
    public bool IsPlayerVisible => HasAudio && !IsEditing && (_tab == RecordingTab.Transcript || _tab == RecordingTab.Translate || IsDiffView);

    public bool IsNoAudioLineVisible => HasNoAudio && !IsEditing && _tab == RecordingTab.Transcript;

    // ── Edit mode ─────────────────────────────────────────────────────────

    public bool IsEditing => _edit is not null;
    public bool IsReading => _edit is null;
    public string EditCaption => _edit?.Caption ?? "";
    public string ChangedText => _edit?.ChangedText ?? "";
    public bool CanUndo => _edit?.CanUndo == true;
    public bool HasEditChanges => _edit?.HasChanges == true;

    public void BeginEdit()
    {
        if (_manifest is null || _viewed is null || _viewedVersionId is null || IsEditing)
            return;
        _edit = new HistoryUiEditSession(_viewedVersionId, _viewed.Segments.Select(s => s.Text.Trim()),
            HistoryUiEditSession.NextVersionIdOf(_manifest));
        foreach (var s in Segments)
        {
            s.EditText = s.Text;
            s.IsChanged = false;
        }
        RaiseEdit();
        RaiseChrome();
    }

    public void SetEditText(SegmentViewModel segment, string text)
    {
        if (_edit is null)
            return;
        _edit.Set(segment.Position, text);
        segment.IsChanged = _edit.IsChanged(segment.Position);
        RaiseEdit();
    }

    public void UndoEdit()
    {
        if (_edit is null)
            return;
        var index = _edit.Undo();
        if (index >= 0 && index < Segments.Count)
        {
            Segments[index].EditText = _edit.Texts[index];
            Segments[index].IsChanged = _edit.IsChanged(index);
        }
        RaiseEdit();
    }

    public void CancelEdit()
    {
        _edit = null;
        RaiseEdit();
        RaiseChrome();
    }

    /// <summary>Saves the edit as a new version (it becomes current) and shows it.</summary>
    public async Task<TranscriptVersion?> SaveEditAsync()
    {
        if (_edit is not { HasChanges: true } edit)
        {
            CancelEdit();
            return null;
        }
        var id = RecordingId;
        var texts = edit.Texts.ToList();
        var version = await Task.Run(() => _store.SaveEdit(id, edit.SourceVersionId, texts));
        _edit = null;
        await LoadAsync(id, version.Id);
        RaiseEdit();
        RaiseChrome();
        return version;
    }

    private void RaiseEdit()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsReading));
        OnPropertyChanged(nameof(EditCaption));
        OnPropertyChanged(nameof(ChangedText));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(HasEditChanges));
    }

    private void RaiseChrome()
    {
        OnPropertyChanged(nameof(IsTranscriptTab));
        OnPropertyChanged(nameof(IsChatsTab));
        OnPropertyChanged(nameof(IsTranslateTab));
        OnPropertyChanged(nameof(IsVersionsList));
        OnPropertyChanged(nameof(IsDiffView));
        OnPropertyChanged(nameof(IsPlayerVisible));
        OnPropertyChanged(nameof(IsNoAudioLineVisible));
    }

    // ── Loading ───────────────────────────────────────────────────────────

    /// <summary>Loads the recording; <paramref name="viewVersion"/> picks the transcript shown (default: current).</summary>
    public async Task LoadAsync(string recordingId, string? viewVersion = null)
    {
        RecordingId = recordingId;
        var data = await Task.Run(() =>
        {
            var entry = _store.Get(recordingId);
            if (entry is null)
                return default;
            var manifest = entry.Manifest;
            var version = viewVersion is not null && manifest.FindVersion(viewVersion) is not null
                ? viewVersion
                : manifest.CurrentVersion;
            TranscriptDocument? transcript = null;
            try
            {
                if (version is not null)
                    transcript = _store.LoadTranscript(recordingId, version);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                Console.WriteLine($"[VoiceType.Uno] Transcript {version} of {recordingId} could not be read: {ex.Message}");
            }
            IReadOnlyList<ChatRecord> chats;
            try
            {
                chats = _chats.ListIncluding(recordingId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                chats = [];
            }
            var audio = _store.GetAudioPath(recordingId);
            var bytes = audio is null ? 0 : new FileInfo(audio).Length;
            return (manifest, version, transcript, chats, audio, bytes);
        });

        if (data.manifest is null)
        {
            IsMissing = true;
            _manifest = null;
            Segments.Clear();
            RaiseAll();
            return;
        }

        IsMissing = false;
        _manifest = data.manifest;
        _viewedVersionId = data.version;
        _viewed = data.transcript;
        _audioPath = data.audio;
        _audioBytes = data.bytes;
        _chatRecords = data.chats;
        _chatCount = data.chats.Count;

        Segments.Clear();
        if (_viewed is not null)
        {
            for (var i = 0; i < _viewed.Segments.Count; i++)
                Segments.Add(new SegmentViewModel(i, _viewed.Segments[i]));
        }

        BuildChats();
        BuildVersions();
        RaiseAll();
        _ = ComputeVersionDiffsAsync();
    }

    private void BuildChats()
    {
        ChatCards.Clear();
        foreach (var chat in _chatRecords)
        {
            var others = chat.Sources.Count - 1;
            var who = others <= 0 ? "Only this recording" : HistoryUiFormat.Count(chat.Sources.Count, "recording");
            var meta = HistoryUiFormat.Join(who, HistoryUiFormat.Count(ChatUiFormat.CountMessages(chat), "message"),
                chat.UpdatedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture));
            var last = chat.Messages.LastOrDefault(m => m.Role == ChatRoles.Assistant)?.Text.Trim() ?? "";
            if (last.Length > 140)
                last = last[..140].TrimEnd() + "…";
            ChatCards.Add(new ChatCardViewModel { Id = chat.Id, SourceCount = chat.Sources.Count, Title = string.IsNullOrWhiteSpace(chat.Title) ? "Chat" : chat.Title, Meta = meta, LastLine = last });
        }
    }

    private void BuildVersions()
    {
        Versions.Clear();
        VersionChoices.Clear();
        if (_manifest is null)
            return;
        var now = DateTimeOffset.Now;
        foreach (var version in _manifest.Versions
                     .Where(v => !HistoryServices.Deletes.Pending.IsVersionHidden(RecordingId, v.Id))
                     .OrderByDescending(v => VersionNumber(v.Id)))
        {
            var chats = _chatRecords.Count(c => c.Sources.Exists(s => s.RecordingId == RecordingId && s.VersionId == version.Id));
            Versions.Add(new VersionCardViewModel(version, _manifest, chats, now));
        }
        foreach (var version in _manifest.Versions.OrderBy(v => VersionNumber(v.Id)))
            VersionChoices.Add(new VersionChoice(version));
    }

    /// <summary>Re-applies pending-delete hiding to the version list.</summary>
    public void RefreshVersions()
    {
        BuildVersions();
        _ = ComputeVersionDiffsAsync();
    }

    /// <summary>Reference version for "vs vN": the parent of an edit, else the previous version.</summary>
    public string? ReferenceOf(string versionId)
    {
        if (_manifest?.FindVersion(versionId) is not { } version)
            return null;
        if (version.Parent is { } parent && _manifest.FindVersion(parent) is not null)
            return parent;
        return _manifest.Versions
            .Where(v => VersionNumber(v.Id) < VersionNumber(versionId))
            .OrderByDescending(v => VersionNumber(v.Id))
            .FirstOrDefault()?.Id;
    }

    private async Task ComputeVersionDiffsAsync()
    {
        var id = RecordingId;
        foreach (var card in Versions.ToList())
        {
            if (ReferenceOf(card.Id) is not { } reference)
                continue;
            try
            {
                var percent = await Task.Run(() =>
                {
                    var a = _store.LoadTranscript(id, reference);
                    var b = _store.LoadTranscript(id, card.Id);
                    return TranscriptDiff.Compute(a, b).ChangedPercent;
                });
                card.DiffText = $"vs {reference} {percent.ToString(percent >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture)}% diff";
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                // A missing transcript file only loses the diff figure.
            }
        }
    }

    public async Task<HistoryDiffView?> BuildDiffAsync(string a, string b)
    {
        var id = RecordingId;
        try
        {
            return await Task.Run(() => HistoryUiDiff.Build(_store.LoadTranscript(id, a), _store.LoadTranscript(id, b)));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Console.WriteLine($"[VoiceType.Uno] Diff {a}→{b} failed: {ex.Message}");
            return null;
        }
    }

    public async Task MakeCurrentAsync(string versionId)
    {
        var id = RecordingId;
        await Task.Run(() => _store.SetCurrentVersion(id, versionId));
        await LoadAsync(id, versionId);
    }

    public async Task RenameAsync(string title)
    {
        var id = RecordingId;
        await Task.Run(() => _store.Rename(id, title));
        await LoadAsync(id, _viewedVersionId);
    }

    public string ChatsHeader => _chatCount == 0 ? "Not used in any chat yet" : "In " + HistoryUiFormat.Count(_chatCount, "chat");

    public string TranslateText => _manifest is null || _manifest.Translations.Count == 0
        ? "No translations yet."
        : string.Join("\n", _manifest.Translations.Select(t =>
            $"→ {t.TargetLanguage.ToUpperInvariant()} · from {t.SourceVersion}"
            + (t.SourceVersion != _manifest.CurrentVersion ? " (outdated)" : "")
            + (t.Status == TranslationStatuses.Complete ? "" : " · " + t.Status)));

    public string VersionsHint => HasAudio
        ? "Re-recognize the saved audio with another model or language; each run adds a version."
        : "The audio is gone, so this recording cannot be re-recognized. Edits still create versions.";

    /// <summary>Index of the segment playing at <paramref name="seconds"/> (last segment that started), or -1.</summary>
    public int SegmentAt(double seconds)
    {
        int lo = 0, hi = Segments.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Segments[mid].Start <= seconds)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(IsMissing), nameof(Title), nameof(Subtitle), nameof(HasAudio), nameof(HasNoAudio),
                     nameof(NoAudioText), nameof(ViewedVersionId), nameof(ViewedVersionLabel), nameof(HasSegments),
                     nameof(HasNoSegments), nameof(ChatsHeader), nameof(TranslateText), nameof(VersionsHint),
                     nameof(ChatCount), nameof(AudioPath),
                 })
            OnPropertyChanged(name);
        RaiseEdit();
        RaiseChrome();
    }

    internal static int VersionNumber(string id) =>
        id.Length > 1 && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
