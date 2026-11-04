using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Services;

/// <summary>
/// Strings shown on the Models page (header, tab labels, download progress lines).
/// Kept apart from the view models so the wording rules are testable without a UI.
/// </summary>
public static class ModelsPageFormatter
{
    /// <summary>Page subtitle, e.g. "2 installed · 1.4 GB used".</summary>
    public static string DescribeInstalled(int installedCount, long bytesUsed)
    {
        if (installedCount <= 0)
            return Loc.T("Models.Page.NoModelsInstalled");

        return bytesUsed > 0
            ? Loc.Plural("Models.Page.InstalledUsed", installedCount, DownloadProgressFormatter.FormatBytes(bytesUsed))
            : Loc.Plural("Models.Page.Installed", installedCount);
    }

    /// <summary>Label of the downloads tab: the count only while something is queued or running.</summary>
    public static string DownloadsTabLabel(int activeJobs) =>
        activeJobs > 0 ? Loc.F("Models.Tabs.DownloadsCount", activeJobs) : Loc.T("Models.Tabs.Downloads");

    /// <summary>Headline of the queue summary card, e.g. "2 active · 1 failed".</summary>
    public static string DescribeQueueHeadline(int active, int failed, int completed, int total)
    {
        if (total <= 0)
            return Loc.T("Models.Downloads.NoDownloads");

        var parts = new List<string>(3);
        if (active > 0)
            parts.Add(Loc.Plural("Models.Downloads.Active", active));
        if (failed > 0)
            parts.Add(Loc.Plural("Models.Downloads.Failed", failed));
        if (completed > 0)
            parts.Add(Loc.Plural("Models.Downloads.Done", completed));

        // Only cancelled jobs are left over.
        return parts.Count == 0 ? Loc.Plural("Models.Downloads.Stopped", total) : string.Join(" · ", parts);
    }

    /// <summary>"1.0 / 2.4 GB"-style transfer; the downloaded bytes alone when the total is unknown.</summary>
    public static string DescribeTransfer(long downloadedBytes, long totalBytes)
    {
        if (totalBytes > 0)
            return $"{DownloadProgressFormatter.FormatBytes(downloadedBytes)} / {DownloadProgressFormatter.FormatBytes(totalBytes)}";

        return downloadedBytes > 0 ? DownloadProgressFormatter.FormatBytes(downloadedBytes) : "";
    }

    /// <summary>Progress line under a card's bar, e.g. "42% · 1.0 / 2.4 GB".</summary>
    public static string DescribeProgress(double percent, long downloadedBytes, long totalBytes)
    {
        var transfer = DescribeTransfer(downloadedBytes, totalBytes);
        if (totalBytes <= 0)
            return transfer;

        return Loc.F("Models.Progress.PercentTransfer", percent, transfer);
    }

    /// <summary>
    /// Speed and time left, e.g. "3.1 MB/s · 7 min". Empty when nothing is moving;
    /// the time is left out when the total size is unknown.
    /// </summary>
    public static string DescribeRate(double bytesPerSecond, long downloadedBytes, long totalBytes)
    {
        if (bytesPerSecond <= 0)
            return "";

        var rate = Loc.F("Models.Progress.Rate", DownloadProgressFormatter.FormatBytes((long)bytesPerSecond));
        if (totalBytes <= 0 || downloadedBytes >= totalBytes)
            return rate;

        var remaining = TimeSpan.FromSeconds((totalBytes - downloadedBytes) / bytesPerSecond);
        return $"{rate} · {FormatTimeLeft(remaining)}";
    }

    /// <summary>Coarse remaining time: "&lt; 1 min", "7 min", "1 h 05 min".</summary>
    public static string FormatTimeLeft(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
            return Loc.T("Models.Progress.LessThanMinute");

        if (remaining < TimeSpan.FromHours(1))
            return Loc.F("Models.Progress.Minutes", (int)Math.Ceiling(remaining.TotalMinutes));

        return Loc.F("Models.Progress.HoursMinutes", (int)remaining.TotalHours, remaining.Minutes);
    }
}
