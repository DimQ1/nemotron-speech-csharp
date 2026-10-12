namespace VoiceType.Uno.History;

/// <summary>A delete waiting for its Undo window to close.</summary>
/// <param name="Token">Handle for <see cref="HistoryUiPendingDeletes.Undo"/>.</param>
/// <param name="Label">Snackbar text, e.g. <c>Deleted 3 recordings</c>.</param>
/// <param name="RecordingIds">Recordings hidden from the list while pending (whole-recording delete).</param>
/// <param name="VersionKeys">Versions hidden while pending, as <c>recordingId/versionId</c>.</param>
/// <param name="DueAt">When the delete is committed.</param>
public sealed record PendingDelete(
    Guid Token,
    string Label,
    IReadOnlyList<string> RecordingIds,
    IReadOnlyList<string> VersionKeys,
    DateTimeOffset DueAt);

/// <summary>
/// Bookkeeping of soft deletes when "Ask before deleting" is off: the items disappear at
/// once, an Undo snackbar stays for <see cref="UndoWindow"/>, and only then is the delete
/// committed. Nothing touches the disk before the commit, so an app that closes inside
/// the window simply keeps the item. Thread-safe; the UI owns the timer and runs the
/// commit actions it gets back from <see cref="TakeDue"/>.
/// </summary>
public sealed class HistoryUiPendingDeletes
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(8);

    private readonly Lock _gate = new();
    private readonly List<(PendingDelete Entry, Action Commit)> _pending = [];
    private readonly TimeProvider _time;

    public HistoryUiPendingDeletes(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Raised (outside the lock) after any add, undo or commit.</summary>
    public event Action? Changed;

    public static string VersionKey(string recordingId, string versionId) => recordingId + "/" + versionId;

    public PendingDelete Schedule(string label, IEnumerable<string>? recordingIds, IEnumerable<string>? versionKeys, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var entry = new PendingDelete(Guid.NewGuid(), label,
            recordingIds?.ToList() ?? [], versionKeys?.ToList() ?? [], _time.GetUtcNow() + UndoWindow);
        lock (_gate)
            _pending.Add((entry, commit));
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Cancels a pending delete; false when it was already committed.</summary>
    public bool Undo(Guid token)
    {
        bool removed;
        lock (_gate)
            removed = _pending.RemoveAll(p => p.Entry.Token == token) > 0;
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>Removes and returns the commits that are due (or all with <paramref name="all"/>).</summary>
    public IReadOnlyList<Action> TakeDue(bool all = false)
    {
        var now = _time.GetUtcNow();
        List<Action> due;
        lock (_gate)
        {
            due = _pending.Where(p => all || p.Entry.DueAt <= now).Select(p => p.Commit).ToList();
            _pending.RemoveAll(p => all || p.Entry.DueAt <= now);
        }
        if (due.Count > 0)
            Changed?.Invoke();
        return due;
    }

    /// <summary>Removes one entry and returns its commit (snackbar closed with ×: delete now).</summary>
    public Action? Take(Guid token)
    {
        Action? commit = null;
        lock (_gate)
        {
            var index = _pending.FindIndex(p => p.Entry.Token == token);
            if (index >= 0)
            {
                commit = _pending[index].Commit;
                _pending.RemoveAt(index);
            }
        }
        if (commit is not null)
            Changed?.Invoke();
        return commit;
    }

    /// <summary>The newest pending delete (the one the snackbar shows), if any.</summary>
    public PendingDelete? Latest
    {
        get
        {
            lock (_gate)
                return _pending.Count == 0 ? null : _pending[^1].Entry;
        }
    }

    public bool IsRecordingHidden(string recordingId)
    {
        lock (_gate)
            return _pending.Exists(p => p.Entry.RecordingIds.Contains(recordingId));
    }

    public bool IsVersionHidden(string recordingId, string versionId)
    {
        var key = VersionKey(recordingId, versionId);
        lock (_gate)
            return _pending.Exists(p => p.Entry.VersionKeys.Contains(key));
    }

    /// <summary>Time until the earliest commit; null when nothing is pending.</summary>
    public TimeSpan? NextDueIn
    {
        get
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                    return null;
                var wait = _pending.Min(p => p.Entry.DueAt) - _time.GetUtcNow();
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            }
        }
    }
}
