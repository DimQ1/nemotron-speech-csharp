using Microsoft.UI.Dispatching;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// The History screens' access to the app-wide stores (registered in <c>App.xaml.cs</c>)
/// and the one pending-delete coordinator they share, so an Undo snackbar shown on the
/// list also covers a delete started on the recording page.
/// </summary>
internal static class HistoryServices
{
    private static HistoryDeletes? s_deletes;

    public static RecordingStore Recordings => App.Services.GetRequiredService<RecordingStore>();

    public static ChatStore Chats => App.Services.GetRequiredService<ChatStore>();

    public static SettingsStore Settings => App.Services.GetRequiredService<SettingsStore>();

    /// <summary>Created on first use on the UI thread (its commit timer needs the dispatcher).</summary>
    public static HistoryDeletes Deletes => s_deletes ??= new HistoryDeletes(DispatcherQueue.GetForCurrentThread());

    /// <summary>Current history settings (live instance; mutate only through <see cref="SettingsStore"/>).</summary>
    public static HistorySettings HistorySettings => Settings.Current.History;

    /// <summary>Persists a change of the History section through the single settings writer.</summary>
    public static void UpdateHistorySettings(Action<HistorySettings> change) =>
        Settings.Post(s => change(s.History),
            ex => Console.WriteLine($"[VoiceType.Uno] History settings could not be saved: {ex.Message}"));

    /// <summary>Deletes recordings now: folders, and their sources in chats (chats keep their messages).</summary>
    public static void DeleteRecordingsNow(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                Recordings.DeleteRecording(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                Console.WriteLine($"[VoiceType.Uno] Recording {id} could not be deleted: {ex.Message}");
            }
            try
            {
                Chats.RemoveRecordingFromAll(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[VoiceType.Uno] Chats could not drop recording {id}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Deletes a non-current version; chats that read exactly that version move to the
    /// current one (design §5.2), the version's translations go with it.
    /// </summary>
    public static void DeleteVersionNow(string recordingId, string versionId)
    {
        var referencing = Chats.ListReferencing(recordingId, versionId);
        Recordings.DeleteVersion(recordingId, versionId);
        var current = Recordings.Get(recordingId)?.Manifest.CurrentVersion;
        if (current is null)
            return;
        foreach (var chat in referencing)
            Chats.AddSource(chat.Id, recordingId, current);
    }
}

/// <summary>
/// UI side of <see cref="HistoryUiPendingDeletes"/>: a dispatcher timer commits due
/// deletes on the thread pool; pages listen to <see cref="Changed"/> to hide rows and
/// show the Undo snackbar.
/// </summary>
internal sealed class HistoryDeletes
{
    private readonly DispatcherQueue? _dispatcher;
    private readonly DispatcherQueueTimer? _timer;

    public HistoryDeletes(DispatcherQueue? dispatcher)
    {
        _dispatcher = dispatcher;
        if (dispatcher is not null)
        {
            _timer = dispatcher.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => CommitDue();
        }
        Pending.Changed += OnPendingChanged;
    }

    public HistoryUiPendingDeletes Pending { get; } = new();

    /// <summary>Raised on the UI thread after a schedule, undo or commit.</summary>
    public event Action? Changed;

    public PendingDelete Schedule(string label, IReadOnlyList<string> recordingIds, IReadOnlyList<string>? versionKeys, Action commit) =>
        Pending.Schedule(label, recordingIds, versionKeys, commit);

    public void Undo(Guid token) => Pending.Undo(token);

    /// <summary>Snackbar closed with ×: the delete happens now.</summary>
    public void CommitNow(Guid token)
    {
        if (Pending.Take(token) is { } commit)
            _ = RunAsync([commit]);
    }

    private void CommitDue() => _ = RunAsync(Pending.TakeDue());

    private static Task RunAsync(IReadOnlyList<Action> commits) =>
        commits.Count == 0
            ? Task.CompletedTask
            : Task.Run(() =>
            {
                foreach (var commit in commits)
                {
                    try
                    {
                        commit();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VoiceType.Uno] Deferred delete failed: {ex.Message}");
                    }
                }
            });

    private void OnPendingChanged()
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
            Rearm();
        else
            _dispatcher.TryEnqueue(Rearm);
    }

    private void Rearm()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            if (Pending.NextDueIn is { } wait)
            {
                _timer.Interval = wait + TimeSpan.FromMilliseconds(50);
                _timer.Start();
            }
        }
        Changed?.Invoke();
    }
}
