using SpeechLib.ModelDownload;

namespace VoiceType.Uno.Services;

/// <summary>Aggregate download-queue progress, already formatted for display.</summary>
public readonly record struct DownloadQueueProgress(bool IsActive, double Percent, string Text)
{
    public static DownloadQueueProgress Empty { get; } = new(false, 0, "");
}

/// <summary>
/// Turns download counters into the strings the main window shows. Split out of the
/// ViewModel so the formatting rules are testable without a UI.
/// </summary>
public static class DownloadProgressFormatter
{
    /// <summary>Aggregate progress over the whole queue (recognition + translation jobs).</summary>
    public static DownloadQueueProgress DescribeQueue(DownloadTotals totals)
    {
        var totalJobs = totals.Active + totals.Completed + totals.Failed;
        if (totals.TotalBytes > 0)
        {
            return new DownloadQueueProgress(
                totals.Active > 0,
                totals.Percent,
                $"Downloading models: {totals.Percent:F0}% " +
                $"({FormatBytes(totals.DownloadedBytes)} / {FormatBytes(totals.TotalBytes)}, " +
                $"{totals.Completed}/{totalJobs} done)");
        }

        return totals.Active > 0
            ? new DownloadQueueProgress(true, totals.Percent, $"Downloading models... ({totals.Completed}/{totalJobs} done)")
            : DownloadQueueProgress.Empty;
    }

    /// <summary>
    /// Status line for a single running job, shown under the model banner.
    /// Returns <c>null</c> when the job is not downloading.
    /// </summary>
    public static string? DescribeJob(DownloadJob job) =>
        job.IsActive
            ? job.FilesTotal > 0
                ? $"Downloading model... {job.Percent:F0}% ({job.FilesDone}/{job.FilesTotal} files)"
                : "Downloading model..."
            : null;

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };
}
