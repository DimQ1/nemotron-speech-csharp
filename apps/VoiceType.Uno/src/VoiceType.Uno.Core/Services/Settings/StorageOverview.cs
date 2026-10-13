using VoiceType.Uno.History;

namespace VoiceType.Uno.Services;

/// <summary>Free and total space of one drive.</summary>
public sealed record DriveSpace(string Root, long FreeBytes, long TotalBytes);

/// <summary>What VoiceType stores, measured: the three folders and the drives they are on.</summary>
public sealed record StorageUsage(
    long ModelsBytes,
    long RecordingsBytes,
    int Recordings,
    long DataBytes,
    IReadOnlyList<DriveSpace> Drives,
    string ModelsRoot,
    string SessionsDir,
    string DataRoot)
{
    public long TotalBytes => ModelsBytes + RecordingsBytes + DataBytes;
}

/// <summary>Measures and words the Storage summary (design §4.1). Measurement is plain file I/O: call it off the UI thread.</summary>
public static class StorageOverview
{
    /// <summary>Measures the three folders of <paramref name="settings"/>. App data excludes models and recordings kept inside the data root.</summary>
    public static StorageUsage Measure(AppSettings settings, string dataRoot, string defaultSessionsDir, string? chatsDir = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var layout = ModelStorageLayout.Resolve(settings);
        var sessions = RecordingStorageMigrator.Resolve(settings, defaultSessionsDir);

        var models = ModelStorageScanner.Summarize(layout).TotalBytes;
        var recordings = HistoryUiStorage.Measure(sessions);
        var data = FolderSize(dataRoot, [layout.Root, sessions]);

        return new StorageUsage(models, recordings.AudioBytes + recordings.TextBytes, recordings.Recordings, data,
            Drives([layout.Root, sessions, dataRoot]), layout.Root, sessions, dataRoot);
    }

    /// <summary>Size of every file under <paramref name="folder"/> except what lies inside one of <paramref name="exclude"/>.</summary>
    public static long FolderSize(string folder, IReadOnlyList<string> exclude)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(folder))
                return 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                if (exclude.Any(e => FolderMover.IsInside(file.FullName, e)))
                    continue;
                total += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read counts for what was read so far.
        }
        return total;
    }

    /// <summary>The distinct drives holding the given folders, with free and total space.</summary>
    public static IReadOnlyList<DriveSpace> Drives(IEnumerable<string> folders)
    {
        var result = new List<DriveSpace>();
        foreach (var folder in folders)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root) || result.Any(d => string.Equals(d.Root, root, ModelStorageLayout.PathComparison)))
                    continue;
                var info = new DriveInfo(root);
                result.Add(new DriveSpace(root, info.AvailableFreeSpace, info.TotalSize));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            {
                // Free space is optional information.
            }
        }
        return result;
    }

    /// <summary><c>4.6 GB in total</c>.</summary>
    public static string TotalText(StorageUsage usage) => HistoryUiFormat.Bytes(usage.TotalBytes) + " in total";

    /// <summary>
    /// <c>41 GB free on C: · 212 GB free on D:</c> with several drives; <c>41 GB free</c> with one drive on a desktop;
    /// <c>41 GB free of 128 GB</c> on a phone.
    /// </summary>
    public static string FreeText(IReadOnlyList<DriveSpace> drives, bool android)
    {
        if (drives.Count == 0)
            return "";
        if (drives.Count == 1)
            return HistoryUiFormat.Bytes(drives[0].FreeBytes) + (android && drives[0].TotalBytes > 0 ? " free of " + HistoryUiFormat.Bytes(drives[0].TotalBytes) : " free");
        return string.Join(" · ", drives.Select(d => $"{HistoryUiFormat.Bytes(d.FreeBytes)} free on {DriveLabel(d.Root)}"));
    }

    /// <summary><c>C:\</c> gives <c>C:</c>; a Unix root stays as it is.</summary>
    public static string DriveLabel(string root) => root.TrimEnd('\\', '/') is { Length: > 0 } t ? t : root;

    /// <summary>Share of the bar for models, recordings and app data: bytes over the capacity of the busiest drive, with a visible minimum.</summary>
    public static (double Models, double Recordings, double Data) Fractions(StorageUsage usage)
    {
        var capacity = (double)(usage.Drives.Count == 0 ? usage.TotalBytes : usage.Drives.Max(d => d.TotalBytes));
        if (capacity <= 0 || usage.TotalBytes <= 0)
            return (0, 0, 0);
        double Share(long bytes) => bytes <= 0 ? 0 : Math.Max(0.012, bytes / capacity);
        var parts = new[] { Share(usage.ModelsBytes), Share(usage.RecordingsBytes), Share(usage.DataBytes) };
        var sum = parts.Sum();
        if (sum > 1)
            parts = parts.Select(p => p / sum).ToArray();
        return (parts[0], parts[1], parts[2]);
    }

    /// <summary>
    /// The Storage line of the hub: <c>Models 3.4 GB on C: · recordings on D:</c> when the folders are on different drives,
    /// otherwise <c>Models 3.4 GB · recordings 1.1 GB</c>.
    /// </summary>
    public static string HubLine(StorageUsage usage)
    {
        var modelsDrive = DriveOf(usage.ModelsRoot);
        var sessionsDrive = DriveOf(usage.SessionsDir);
        var differ = !string.Equals(modelsDrive, sessionsDrive, ModelStorageLayout.PathComparison);
        return differ
            ? $"Models {HistoryUiFormat.Bytes(usage.ModelsBytes)} on {DriveLabel(modelsDrive)} · recordings on {DriveLabel(sessionsDrive)}"
            : $"Models {HistoryUiFormat.Bytes(usage.ModelsBytes)} · recordings {HistoryUiFormat.Bytes(usage.RecordingsBytes)}";
    }

    private static string DriveOf(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "";
        }
    }
}
