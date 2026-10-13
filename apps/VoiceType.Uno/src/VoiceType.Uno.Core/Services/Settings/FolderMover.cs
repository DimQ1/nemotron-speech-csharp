namespace VoiceType.Uno.Services;

/// <summary>One item (folder or file) that was moved.</summary>
public sealed record FolderMove(string From, string To);

/// <summary>One item that stayed where it was.</summary>
public sealed record FolderMoveSkip(string From, string To, ModelMoveSkipReason Reason);

/// <summary>What <see cref="FolderMover.MoveChildren"/> did.</summary>
public sealed record FolderMoveReport(IReadOnlyList<FolderMove> Moved, IReadOnlyList<FolderMoveSkip> Skipped)
{
    public static FolderMoveReport Empty { get; } = new([], []);

    public bool IsEmpty => Moved.Count == 0 && Skipped.Count == 0;

    /// <summary>Everything that had to move has moved.</summary>
    public bool IsComplete => Skipped.Count == 0;

    /// <summary>One line for the log.</summary>
    public string Summary =>
        IsEmpty ? "nothing to do"
        : $"{Moved.Count} moved, {Skipped.Count} left in place"
          + (Skipped.Count == 0 ? "" : " (" + string.Join(", ", Skipped.GroupBy(s => s.Reason).Select(g => $"{g.Count()} {g.Key}")) + ")");
}

/// <summary>
/// The safe move shared by the recordings and the app data migrations: moves the children of one folder into another by
/// renaming (instant on one drive), never overwrites, never deletes anything it did not copy itself, and leaves whatever
/// is locked for the next start. Copying across drives is off unless the caller allows it (app data is small; recordings
/// and models can be many GB and are not copied automatically).
/// </summary>
public static class FolderMover
{
    public static FolderMoveReport MoveChildren(
        string from,
        string to,
        bool allowCopyAcrossDrives = false,
        Func<string, bool>? exclude = null,
        Action<string>? log = null)
        => MoveChildren(from, to, allowCopyAcrossDrives, exclude, log, ModelStorageText.SameDrive);

    /// <summary>The same, with the drive test replaced (tests use it to exercise the copy path on one drive).</summary>
    internal static FolderMoveReport MoveChildren(
        string from,
        string to,
        bool allowCopyAcrossDrives,
        Func<string, bool>? exclude,
        Action<string>? log,
        Func<string, string, bool> sameDriveTest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        if (PathsEqual(from, to) || !Directory.Exists(from))
            return FolderMoveReport.Empty;

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(from).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"cannot list {from}: {ex.Message}");
            return FolderMoveReport.Empty;
        }

        var moved = new List<FolderMove>();
        var skipped = new List<FolderMoveSkip>();
        var nested = IsInside(to, from) || IsInside(from, to);
        var sameDrive = sameDriveTest(from, to);

        foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            if (exclude?.Invoke(entry) == true)
                continue;

            var destination = Path.Combine(to, Path.GetFileName(entry));
            if (nested)
            {
                skipped.Add(new FolderMoveSkip(entry, destination, ModelMoveSkipReason.Nested));
                log?.Invoke($"left {entry}: nested folders");
                continue;
            }

            if (!sameDrive && !allowCopyAcrossDrives)
            {
                skipped.Add(new FolderMoveSkip(entry, destination, ModelMoveSkipReason.OtherDrive));
                log?.Invoke($"left {entry}: another drive");
                continue;
            }

            if (Directory.Exists(destination) || File.Exists(destination))
            {
                skipped.Add(new FolderMoveSkip(entry, destination, ModelMoveSkipReason.DestinationExists));
                log?.Invoke($"left {entry}: {destination} already exists");
                continue;
            }

            if (MoveOne(entry, destination, sameDrive, log))
            {
                moved.Add(new FolderMove(entry, destination));
                log?.Invoke($"moved {entry} -> {destination}");
            }
            else
            {
                skipped.Add(new FolderMoveSkip(entry, destination, ModelMoveSkipReason.InUse));
            }
        }

        var report = new FolderMoveReport(moved, skipped);
        log?.Invoke($"move {from} -> {to}: {report.Summary}");
        return report;
    }

    /// <summary>
    /// Undoes the moves of an incomplete <paramref name="report"/> so the folder is not left half here and half there (the
    /// app only ever reads one of them): every moved item goes back, in reverse order, unless something took its old place.
    /// </summary>
    public static int Revert(FolderMoveReport report, bool allowCopyAcrossDrives = false, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var restored = 0;
        foreach (var move in report.Moved.Reverse())
        {
            if (Directory.Exists(move.From) || File.Exists(move.From) || !(Directory.Exists(move.To) || File.Exists(move.To)))
                continue;
            var sameDrive = ModelStorageText.SameDrive(move.To, move.From);
            if (!sameDrive && !allowCopyAcrossDrives)
                continue;
            if (MoveOne(move.To, move.From, sameDrive, log))
            {
                restored++;
                log?.Invoke($"moved back {move.To} -> {move.From}");
            }
        }
        return restored;
    }

    private static bool MoveOne(string source, string destination, bool sameDrive, Action<string>? log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (sameDrive)
            {
                if (Directory.Exists(source))
                    Directory.Move(source, destination);
                else
                    File.Move(source, destination, overwrite: false);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"left {source}: {ex.Message}");
            return false;
        }

        return CopyThenRemove(source, destination, log);
    }

    /// <summary>
    /// Another drive: copies the item to a place that does not exist yet, checks it is complete, and only then removes the
    /// source. When the source cannot be removed (in use) the copy this call made is undone, so nothing ends up twice.
    /// </summary>
    private static bool CopyThenRemove(string source, string destination, Action<string>? log)
    {
        try
        {
            if (Directory.Exists(source))
                CopyDirectory(source, destination);
            else
                File.Copy(source, destination, overwrite: false);

            if (SizeOf(source) != SizeOf(destination))
                throw new IOException("the copy is not the same size as the original");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"left {source}: copy failed: {ex.Message}");
            TryDelete(destination);
            return false;
        }

        try
        {
            if (Directory.Exists(source))
                Directory.Delete(source, recursive: true);
            else
                File.Delete(source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"left {source}: cannot remove it after copying: {ex.Message}");
            TryDelete(destination);
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static long SizeOf(string path)
    {
        if (File.Exists(path))
            return new FileInfo(path).Length;
        return Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the partial copy is ours, a leftover is only clutter.
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        try
        {
            var p = Trim(Path.GetFullPath(path));
            var f = Trim(Path.GetFullPath(folder));
            return string.Equals(p, f, ModelStorageLayout.PathComparison)
                || p.StartsWith(f + Path.DirectorySeparatorChar, ModelStorageLayout.PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    public static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Trim(Path.GetFullPath(a)), Trim(Path.GetFullPath(b)), ModelStorageLayout.PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
