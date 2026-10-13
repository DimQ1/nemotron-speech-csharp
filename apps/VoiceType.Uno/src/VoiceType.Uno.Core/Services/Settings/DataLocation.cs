namespace VoiceType.Uno.Services;

/// <summary>Where the app data folder is, and why.</summary>
/// <param name="Root">The folder in use.</param>
/// <param name="Note">A line for the App data card, or empty: the saved folder was not available, or a move did not finish.</param>
/// <param name="IsCustom">The folder is not the default one.</param>
public sealed record DataRootResolution(string Root, string Note, bool IsCustom);

/// <summary>
/// The app data folder is the one thing <c>settings.json</c> cannot say, because the file lives inside it. A one-line pointer
/// <c>data-location.txt</c> in the DEFAULT root says where the data really is; <see cref="AppPaths"/> reads it first.
/// "Move app data" writes <c>data-location.pending.txt</c> there, and the next start (before anything opens a data file)
/// moves the data and flips the pointer.
/// </summary>
/// <remarks>
/// Models and recordings in their own folders stay where they are. When they live inside the old data root they are first
/// pinned into <see cref="AppSettings.ModelsRootPath"/> / <see cref="AppSettings.SessionsPath"/>, so the move leaves them
/// behind instead of losing them. Nothing is overwritten; items that cannot move stay and a note says so.
/// </remarks>
public static class DataLocation
{
    public const string PointerFileName = "data-location.txt";
    public const string PendingFileName = "data-location.pending.txt";
    public const string NoteFileName = "data-location.note.txt";
    public const string LogFileName = "data-location.log";

    private static readonly string[] PointerFiles = [PointerFileName, PendingFileName, NoteFileName, LogFileName];

    /// <summary>The folder in use: the pointer's folder when it is usable, else <paramref name="defaultRoot"/> (with a note).</summary>
    public static DataRootResolution Resolve(string defaultRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultRoot);
        var fallbackNote = ReadFirstLine(Path.Combine(defaultRoot, NoteFileName)) ?? "";
        var wanted = ReadFirstLine(Path.Combine(defaultRoot, PointerFileName));
        if (string.IsNullOrWhiteSpace(wanted))
            return new DataRootResolution(defaultRoot, fallbackNote, false);

        try
        {
            if (!Path.IsPathRooted(wanted))
                throw new ArgumentException("not an absolute path");
            var full = Path.GetFullPath(wanted);
            Directory.CreateDirectory(full);
            return FolderMover.PathsEqual(full, defaultRoot)
                ? new DataRootResolution(defaultRoot, fallbackNote, false)
                : new DataRootResolution(full, fallbackNote, true);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return new DataRootResolution(defaultRoot, StorageFolderText.DataUnavailableNote(wanted, defaultRoot), false);
        }
    }

    /// <summary>The folder a "Move on restart" is waiting to move the data to, or null.</summary>
    public static string? PendingTarget(string defaultRoot) =>
        ReadFirstLine(Path.Combine(defaultRoot, PendingFileName)) is { Length: > 0 } line ? line : null;

    /// <summary>Remembers the user's choice; the next start moves the data.</summary>
    public static void RequestMove(string defaultRoot, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllText(Path.Combine(defaultRoot, PendingFileName), Path.GetFullPath(target.Trim()));
        TryDelete(Path.Combine(defaultRoot, NoteFileName));
    }

    /// <summary>"Don't move": forgets a pending move.</summary>
    public static void CancelMove(string defaultRoot) => TryDelete(Path.Combine(defaultRoot, PendingFileName));

    /// <summary>Whether <paramref name="path"/> is one of the pointer files kept in the default root (never moved).</summary>
    public static bool IsPointerFile(string path) =>
        PointerFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Start-up entry point, before <see cref="AppPaths.DataRoot"/> is first read: finishes a pending move of the app data.
    /// Returns what happened (empty when nothing was pending).
    /// </summary>
    public static FolderMoveReport RunAtStartup(string defaultRoot, Action<string>? log = null)
    {
        var pending = PendingTarget(defaultRoot);
        if (pending is null)
            return FolderMoveReport.Empty;

        log ??= message => Log(defaultRoot, message);
        // The request is consumed whatever happens: a failed move is reported, not retried forever.
        CancelMove(defaultRoot);

        string target;
        var current = Resolve(defaultRoot);
        try
        {
            target = Path.GetFullPath(pending);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            log($"pending data folder '{pending}' is not usable: {ex.Message}");
            return FolderMoveReport.Empty;
        }

        if (FolderMover.PathsEqual(current.Root, target))
            return FolderMoveReport.Empty;

        FolderMoveReport report;
        try
        {
            Directory.CreateDirectory(target);
            PinModelsAndRecordings(current.Root, log);
            var pinned = PinnedFolders(current.Root);
            report = FolderMover.MoveChildren(
                current.Root, target, allowCopyAcrossDrives: true,
                exclude: entry => IsPointerFile(entry) || pinned.Any(p => FolderMover.IsInside(p, entry)),
                log: log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log($"app data stays in {current.Root}: {ex.Message}");
            WriteNote(defaultRoot, $"App data was not moved to {target}: {ex.Message} It stays in {current.Root}.");
            return FolderMoveReport.Empty;
        }

        if (report.IsComplete)
        {
            SetPointer(defaultRoot, target);
            TryDelete(Path.Combine(defaultRoot, NoteFileName));
        }
        else
        {
            // All or nothing: the app reads one folder only, so a half-moved set would hide settings or chats.
            FolderMover.Revert(report, allowCopyAcrossDrives: true, log);
            var note = StorageFolderText.DataIncompleteNote(target, current.Root, report.Skipped);
            log($"app data stays in {current.Root}: {note}");
            WriteNote(defaultRoot, note);
        }
        return report;
    }

    /// <summary>Points the data at <paramref name="root"/>; the default root removes the pointer.</summary>
    public static void SetPointer(string defaultRoot, string root)
    {
        var file = Path.Combine(defaultRoot, PointerFileName);
        if (FolderMover.PathsEqual(root, defaultRoot))
        {
            TryDelete(file);
            return;
        }
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllText(file, Path.GetFullPath(root));
    }

    /// <summary>
    /// Models and recordings that live inside the data root under their default names are written into the settings as
    /// explicit folders before the data moves, so they stay where they are and keep being found.
    /// </summary>
    internal static void PinModelsAndRecordings(string dataRoot, Action<string>? log)
    {
        var file = Path.Combine(dataRoot, "settings.json");
        var service = new SettingsService(file);
        var settings = service.Load();
        var changed = false;

        var models = Path.Combine(dataRoot, "models");
        if (string.IsNullOrWhiteSpace(settings.ModelsRootPath) && HasContent(models))
        {
            settings.ModelsRootPath = models;
            changed = true;
        }
        var sessions = Path.Combine(dataRoot, "sessions");
        if (string.IsNullOrWhiteSpace(settings.SessionsPath) && HasContent(sessions))
        {
            settings.SessionsPath = sessions;
            changed = true;
        }

        if (changed)
        {
            service.Save(settings);
            log?.Invoke($"pinned models '{settings.ModelsRootPath}' and recordings '{settings.SessionsPath}' before moving the data");
        }
    }

    private static IReadOnlyList<string> PinnedFolders(string dataRoot)
    {
        var settings = new SettingsService(Path.Combine(dataRoot, "settings.json")).Load();
        var pinned = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ModelsRootPath))
            pinned.Add(settings.ModelsRootPath.Trim());
        if (!string.IsNullOrWhiteSpace(settings.SessionsPath))
            pinned.Add(settings.SessionsPath.Trim());
        if (!string.IsNullOrWhiteSpace(settings.PendingModelsRoot))
            pinned.Add(settings.PendingModelsRoot.Trim());
        if (!string.IsNullOrWhiteSpace(settings.PendingSessionsPath))
            pinned.Add(settings.PendingSessionsPath.Trim());
        return pinned;
    }

    private static bool HasContent(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The note to show on the App data card (move did not finish, or the saved folder is unavailable).</summary>
    public static string ReadNote(string defaultRoot) => Resolve(defaultRoot).Note;

    private static void WriteNote(string defaultRoot, string note)
    {
        try
        {
            File.WriteAllText(Path.Combine(defaultRoot, NoteFileName), note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The note is a courtesy; the log has the details.
        }
    }

    private static void Log(string defaultRoot, string message)
    {
        try
        {
            Directory.CreateDirectory(defaultRoot);
            File.AppendAllText(Path.Combine(defaultRoot, LogFileName), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort file logging.
        }
    }

    private static string? ReadFirstLine(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            return File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Our own marker file: a leftover is harmless.
        }
    }
}
