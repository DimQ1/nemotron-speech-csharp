using SpeechLib.Diarization;
using SpeechLib.ModelDownload;
using SpeechLib.ModelFormats;

namespace VoiceType.Uno.Services;

/// <summary>The user's answer to "Move models to the new folder?".</summary>
public enum ModelRootChoice { Keep, MoveOnRestart, Cancel }

/// <summary>One folder or file that was moved.</summary>
public sealed record ModelMove(ModelKind Kind, string From, string To);

/// <summary>Why an item was left where it was.</summary>
public enum ModelMoveSkipReason
{
    /// <summary>The destination already exists: nothing is overwritten.</summary>
    DestinationExists,

    /// <summary>The item is in use (a model is loaded or a download is writing it); the next start tries again.</summary>
    InUse,

    /// <summary>Source and destination are on different drives; moving would mean copying many GB, which is not done automatically.</summary>
    OtherDrive,

    /// <summary>The new folder is inside the old one or the other way round.</summary>
    Nested,
}

/// <summary>One item that was not moved.</summary>
public sealed record ModelMoveSkip(ModelKind Kind, string From, string To, ModelMoveSkipReason Reason);

/// <summary>What a migration or a root move did.</summary>
public sealed record ModelMigrationReport(
    IReadOnlyList<ModelMove> Moved,
    IReadOnlyList<ModelMoveSkip> Skipped,
    bool SettingsChanged)
{
    public static ModelMigrationReport Empty { get; } = new([], [], false);

    /// <summary>Nothing happened and nothing is pending.</summary>
    public bool IsEmpty => Moved.Count == 0 && Skipped.Count == 0;

    /// <summary>Everything that had to move has moved.</summary>
    public bool IsComplete => Skipped.Count == 0;

    /// <summary>Items left in place because the destination exists.</summary>
    public IReadOnlyList<ModelMoveSkip> Conflicts => Skipped.Where(s => s.Reason == ModelMoveSkipReason.DestinationExists).ToList();

    /// <summary>Items left in place because they are in use (retried at the next start).</summary>
    public IReadOnlyList<ModelMoveSkip> Locked => Skipped.Where(s => s.Reason == ModelMoveSkipReason.InUse).ToList();

    /// <summary>One line for the log.</summary>
    public string Summary =>
        IsEmpty ? "nothing to do"
        : $"{Moved.Count} moved, {Skipped.Count} left in place"
          + (Skipped.Count == 0 ? "" : " (" + string.Join(", ", Skipped.GroupBy(s => s.Reason).Select(g => $"{g.Count()} {Describe(g.Key)}")) + ")");

    private static string Describe(ModelMoveSkipReason reason) => reason switch
    {
        ModelMoveSkipReason.DestinationExists => "already exist at the destination",
        ModelMoveSkipReason.InUse => "in use",
        ModelMoveSkipReason.OtherDrive => "on another drive",
        _ => "nested folders",
    };
}

/// <summary>
/// One-time, idempotent, never-destructive moves of model files:
/// <see cref="Migrate"/> sorts the flat layout of older versions into <c>asr/</c>, <c>llm/</c> and <c>diarization/</c>, and
/// <see cref="MoveRoot"/> moves the three folders to a new models root. Both use plain renames on one drive (instant, no
/// copy), never overwrite, never delete, and leave anything locked for the next start.
/// </summary>
public static class ModelStorageMigrator
{
    /// <summary>
    /// Moves the legacy flat content of <paramref name="layout"/>'s root into its kind folders: speech model folders into
    /// <c>asr/</c>, <c>*.litertlm</c> files (with <c>.part</c> and the engine cache beside them) into <c>llm/</c>, and
    /// <c>nemotron-3-diarization-onnx*</c> folders into <c>diarization/</c>. Updates <see cref="AppSettings.ModelPath"/> when
    /// it pointed into a moved folder. Other content of the root is left alone.
    /// </summary>
    public static ModelMigrationReport Migrate(ModelStorageLayout layout, AppSettings? settings = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!Directory.Exists(layout.Root))
            return ModelMigrationReport.Empty;

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(layout.Root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"cannot list {layout.Root}: {ex.Message}");
            return ModelMigrationReport.Empty;
        }

        var moved = new List<ModelMove>();
        var skipped = new List<ModelMoveSkip>();
        var settingsChanged = false;
        foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry) && IsKindFolderName(name))
                continue;

            if (Classify(entry) is not { } kind)
                continue;

            var destination = Path.Combine(layout.DirOf(kind), name);
            if (MoveOne(kind, entry, destination, moved, skipped, log))
                settingsChanged |= RebasePaths(settings, entry, destination);
        }

        log?.Invoke($"migration of {layout.Root}: {new ModelMigrationReport(moved, skipped, settingsChanged).Summary}");
        return new ModelMigrationReport(moved, skipped, settingsChanged);
    }

    /// <summary>
    /// Moves the <c>asr/</c>, <c>llm/</c> and <c>diarization/</c> content of <paramref name="from"/> into the same folders
    /// of <paramref name="to"/>. Same drive only (a rename); items on another drive, nested roots and existing destinations
    /// are left and reported. <see cref="AppSettings.ModelPath"/> follows moved folders.
    /// </summary>
    public static ModelMigrationReport MoveRoot(ModelStorageLayout from, ModelStorageLayout to, AppSettings? settings = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (PathsEqual(from.Root, to.Root) || !Directory.Exists(from.Root))
            return ModelMigrationReport.Empty;

        var moved = new List<ModelMove>();
        var skipped = new List<ModelMoveSkip>();
        var settingsChanged = false;
        var nested = from.Contains(to.Root) || to.Contains(from.Root);
        var sameDrive = SameDrive(from.Root, to.Root);

        foreach (var kind in Enum.GetValues<ModelKind>())
        {
            var source = from.DirOf(kind);
            if (!Directory.Exists(source))
                continue;

            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(source).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"cannot list {source}: {ex.Message}");
                continue;
            }

            foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                var destination = Path.Combine(to.DirOf(kind), Path.GetFileName(entry));
                if (nested || !sameDrive)
                {
                    var reason = nested ? ModelMoveSkipReason.Nested : ModelMoveSkipReason.OtherDrive;
                    skipped.Add(new ModelMoveSkip(kind, entry, destination, reason));
                    log?.Invoke($"left {entry}: {reason}");
                    continue;
                }

                if (MoveOne(kind, entry, destination, moved, skipped, log))
                    settingsChanged |= RebasePaths(settings, entry, destination);
            }
        }

        var report = new ModelMigrationReport(moved, skipped, settingsChanged);
        log?.Invoke($"move {from.Root} -> {to.Root}: {report.Summary}");
        return report;
    }

    /// <summary>
    /// Start-up entry point: finishes a pending "Move models" request, then sorts the flat layout of the root in use.
    /// Settings are edited in place; the caller saves them when <see cref="ModelMigrationReport.SettingsChanged"/>.
    /// </summary>
    public static ModelMigrationReport RunAtStartup(AppSettings settings, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var moved = new List<ModelMove>();
        var skipped = new List<ModelMoveSkip>();
        var changed = false;

        var current = ModelStorageLayout.Resolve(settings);
        if (!string.IsNullOrWhiteSpace(settings.PendingModelsRoot))
        {
            ModelStorageLayout target;
            try
            {
                target = new ModelStorageLayout(settings.PendingModelsRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                log?.Invoke($"pending models folder '{settings.PendingModelsRoot}' is not usable: {ex.Message}");
                settings.PendingModelsRoot = "";
                return new ModelMigrationReport([], [], true);
            }

            // Sort the old root first so its asr/llm/diarization folders hold everything.
            var sort = Migrate(current, settings, log);
            moved.AddRange(sort.Moved);
            skipped.AddRange(sort.Skipped);
            changed |= sort.SettingsChanged;

            Directory.CreateDirectory(target.Root);
            var move = MoveRoot(current, target, settings, log);
            moved.AddRange(move.Moved);
            skipped.AddRange(move.Skipped);
            changed |= move.SettingsChanged;

            settings.PendingModelsRoot = "";
            changed = true;
            if (move.IsComplete && sort.IsComplete)
            {
                settings.ModelsRootPath = target.Root;
                settings.ModelStorageNote = "";
                current = target;
            }
            else
            {
                settings.ModelStorageNote = ModelStorageText.MoveIncompleteNote(target.Root, current.Root, move.Skipped.Concat(sort.Skipped).ToList());
                log?.Invoke($"models stay in {current.Root}: {settings.ModelStorageNote}");
            }
        }

        var legacy = Migrate(current, settings, log);
        moved.AddRange(legacy.Moved);
        skipped.AddRange(legacy.Skipped);
        changed |= legacy.SettingsChanged;
        return new ModelMigrationReport(moved, skipped, changed);
    }

    /// <summary>
    /// What to do with existing models when the user picks another storage folder in Settings:
    /// <see cref="MoveOnRestart"/> keeps the old folder in use now and moves at the next start (nothing is loaded then, so
    /// no file is locked); <see cref="Keep"/> switches at once and leaves the models where they are;
    /// <see cref="Cancel"/> drops the folder change.
    /// </summary>
    public static void ApplyRootChoice(AppSettings settings, AppSettings original, ModelRootChoice choice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(original);
        switch (choice)
        {
            case ModelRootChoice.MoveOnRestart:
                settings.PendingModelsRoot = settings.ModelsRootPath;
                settings.ModelsRootPath = original.ModelsRootPath;
                break;
            case ModelRootChoice.Cancel:
                settings.ModelsRootPath = original.ModelsRootPath;
                settings.PendingModelsRoot = original.PendingModelsRoot;
                break;
            default:
                // Only a real folder change replaces an earlier pending move; saving other settings keeps it.
                if (!PathsEqual(ModelStorageLayout.Resolve(settings).Root, ModelStorageLayout.Resolve(original).Root))
                {
                    settings.PendingModelsRoot = "";
                    settings.ModelStorageNote = "";
                }
                break;
        }
    }

    /// <summary>The kind a flat entry of the root belongs to, or null when it is none of ours.</summary>
    internal static ModelKind? Classify(string entry)
    {
        var name = Path.GetFileName(entry);
        if (Directory.Exists(entry))
        {
            if (name.StartsWith(DiarizationModelLocator.FolderName, StringComparison.OrdinalIgnoreCase))
                return ModelKind.Speaker;
            if (string.Equals(name, KnownModels.DefaultFolder, StringComparison.OrdinalIgnoreCase)
                || ModelCatalog.FindBySubfolder(name) is not null
                || ModelFolderScanner.IsModelDirectory(entry))
                return ModelKind.Speech;
            return null;
        }

        // The model file, an interrupted download (.part/.download) and the engine's caches next to it.
        return File.Exists(entry) && name.Contains(".litertlm", StringComparison.OrdinalIgnoreCase) ? ModelKind.Language : null;
    }

    private static bool IsKindFolderName(string name) =>
        string.Equals(name, ModelStorageLayout.AsrFolderName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ModelStorageLayout.LlmFolderName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ModelStorageLayout.DiarizationFolderName, StringComparison.OrdinalIgnoreCase);

    private static bool MoveOne(ModelKind kind, string source, string destination, List<ModelMove> moved, List<ModelMoveSkip> skipped, Action<string>? log)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            skipped.Add(new ModelMoveSkip(kind, source, destination, ModelMoveSkipReason.DestinationExists));
            log?.Invoke($"left {source}: {destination} already exists");
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(source))
                Directory.Move(source, destination);
            else
                File.Move(source, destination, overwrite: false);
            moved.Add(new ModelMove(kind, source, destination));
            log?.Invoke($"moved {source} -> {destination}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new ModelMoveSkip(kind, source, destination, ModelMoveSkipReason.InUse));
            log?.Invoke($"left {source}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Re-points <see cref="AppSettings.ModelPath"/> when it was the moved item or inside it.</summary>
    private static bool RebasePaths(AppSettings? settings, string from, string to)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.ModelPath))
            return false;
        try
        {
            var current = Path.GetFullPath(settings.ModelPath);
            var source = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (PathsEqual(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), source))
            {
                settings.ModelPath = to;
                return true;
            }

            var prefix = source + Path.DirectorySeparatorChar;
            if (current.StartsWith(prefix, ModelStorageLayout.PathComparison))
            {
                settings.ModelPath = Path.Combine(to, current[prefix.Length..]);
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // An unparsable stored path is left as it was.
        }
        return false;
    }

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), ModelStorageLayout.PathComparison);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            ModelStorageLayout.PathComparison);
}
