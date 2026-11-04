using SpeechLib.ModelDownload;
using VoiceType.Uno.Localization;

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
                Loc.F("Main.Download.QueueBytes", totals.Percent, FormatBytes(totals.DownloadedBytes),
                    FormatBytes(totals.TotalBytes), totals.Completed, totalJobs));
        }

        return totals.Active > 0
            ? new DownloadQueueProgress(true, totals.Percent, Loc.F("Main.Download.Queue", totals.Completed, totalJobs))
            : DownloadQueueProgress.Empty;
    }

    /// <summary>
    /// Status line for a single running job, shown under the model banner.
    /// Returns <c>null</c> when the job is not downloading.
    /// </summary>
    public static string? DescribeJob(DownloadJob job) =>
        job.IsActive
            ? job.FilesTotal > 0
                ? Loc.F("Main.Download.JobFiles", job.Percent, job.FilesDone, job.FilesTotal)
                : Loc.T("Main.Download.Job")
            : null;

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => Loc.F("Main.Bytes.GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => Loc.F("Main.Bytes.MB", bytes / (double)(1L << 20)),
        >= 1L << 10 => Loc.F("Main.Bytes.KB", bytes / (double)(1L << 10)),
        _ => Loc.F("Main.Bytes.B", bytes)
    };
}
