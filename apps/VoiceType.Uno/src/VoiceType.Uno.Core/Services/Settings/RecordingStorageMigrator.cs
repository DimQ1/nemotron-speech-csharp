namespace VoiceType.Uno.Services;

/// <summary>
/// "Move recordings to the new folder?" in Settings › Storage. Same safe contract as <see cref="ModelStorageMigrator"/>:
/// the change is a <see cref="AppSettings.PendingSessionsPath"/> that the next start carries out before the history store
/// opens (nothing is playing or being written then), as plain renames on one drive; nothing is overwritten or deleted, and
/// whatever cannot move stays where it is with a note.
/// </summary>
public static class RecordingStorageMigrator
{
    /// <summary>The recordings folder the settings name now (<c>sessions/</c> of the data root when empty).</summary>
    public static string Resolve(AppSettings settings, string defaultDir)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(settings.SessionsPath) ? defaultDir : settings.SessionsPath.Trim());
    }

    /// <summary>
    /// Start-up entry point: finishes a pending "Move on restart". Settings are edited in place; returns true
    /// when they changed and the caller has to save them.
    /// </summary>
    public static bool RunAtStartup(AppSettings settings, string defaultDir, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.PendingSessionsPath))
            return false;

        string target, current;
        try
        {
            target = Path.GetFullPath(settings.PendingSessionsPath.Trim());
            current = Resolve(settings, defaultDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            log?.Invoke($"pending recordings folder '{settings.PendingSessionsPath}' is not usable: {ex.Message}");
            settings.PendingSessionsPath = "";
            return true;
        }

        settings.PendingSessionsPath = "";
        if (FolderMover.PathsEqual(current, target))
            return true;

        FolderMoveReport report;
        try
        {
            Directory.CreateDirectory(target);
            report = FolderMover.MoveChildren(current, target, allowCopyAcrossDrives: false, log: log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"recordings stay in {current}: {ex.Message}");
            settings.SessionsStorageNote = $"Recordings were not moved to {target}: {ex.Message} They stay in {current}.";
            return true;
        }

        if (report.IsComplete)
        {
            settings.SessionsPath = target;
            settings.SessionsStorageNote = "";
        }
        else
        {
            // All or nothing: a half-moved history would show only the recordings that stayed.
            FolderMover.Revert(report, allowCopyAcrossDrives: false, log);
            settings.SessionsStorageNote = StorageFolderText.RecordingsIncompleteNote(target, current, report.Skipped);
            log?.Invoke($"recordings stay in {current}: {settings.SessionsStorageNote}");
        }
        return true;
    }

    /// <summary>
    /// What to do with the recordings already stored when the user picks another folder:
    /// <see cref="ModelRootChoice.MoveOnRestart"/> keeps the old folder in use and moves at the next start;
    /// <see cref="ModelRootChoice.Keep"/> switches at once and leaves the old recordings where they are (they leave History
    /// until the old folder is chosen again); <see cref="ModelRootChoice.Cancel"/> drops the change.
    /// </summary>
    public static void ApplyChoice(AppSettings settings, string newFolder, ModelRootChoice choice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (choice)
        {
            case ModelRootChoice.MoveOnRestart:
                settings.PendingSessionsPath = newFolder;
                break;
            case ModelRootChoice.Keep:
                settings.SessionsPath = newFolder;
                settings.PendingSessionsPath = "";
                settings.SessionsStorageNote = "";
                break;
        }
    }

    /// <summary>"Don't move": drops a pending move.</summary>
    public static void CancelPending(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.PendingSessionsPath = "";
    }

    /// <summary>Whether the question is needed: the folders differ and the old one holds recordings.</summary>
    public static bool NeedsQuestion(string oldFolder, string newFolder, int recordingsInOld) =>
        recordingsInOld > 0 && !string.IsNullOrWhiteSpace(newFolder) && !FolderMover.PathsEqual(oldFolder, newFolder);
}
