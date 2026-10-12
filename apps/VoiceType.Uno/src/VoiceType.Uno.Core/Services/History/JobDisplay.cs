using System.Globalization;

namespace VoiceType.Uno.History;

/// <summary>Texts of the Queue page and the Reprocess sheet (design §4.5, §9.5).</summary>
public static class JobDisplay
{
    /// <summary>
    /// Real-time factor assumed for a model without a measured speed. The catalog RTFs were
    /// measured on whole utterances; streaming re-recognition is slower, see <see cref="StreamingOverhead"/>.
    /// </summary>
    public const double DefaultRealTimeFactor = 0.5;

    /// <summary>
    /// Re-recognition feeds the streaming path (one encoder pass per 1 s step), a little slower
    /// than the catalog's whole-utterance RTF (Parakeet INT4: 0.19–0.23 measured vs 0.186).
    /// </summary>
    public const double StreamingOverhead = 1.25;

    public static string KindTitle(HistoryJobKind kind) => kind switch
    {
        HistoryJobKind.Rerecognize => "Re-recognize",
        HistoryJobKind.Translate => "Translate",
        HistoryJobKind.AutoTitle => "Title",
        HistoryJobKind.Summarize => "Prepare chat",
        _ => "Job from a newer VoiceType",
    };

    /// <summary>Chips for a job's start condition, e.g. <c>Tonight 01:00–06:00</c>, <c>When charging</c>.</summary>
    public static IReadOnlyList<string> ConditionChips(JobCondition? condition, DateTimeOffset now)
    {
        var chips = new List<string>();
        if (condition is null)
            return chips;

        switch (condition.Kind)
        {
            case JobConditionKind.WhenIdle:
                chips.Add("When idle");
                break;
            case JobConditionKind.WhenCharging:
                chips.Add("When charging");
                break;
            case JobConditionKind.AtTime:
                if (condition.NotBefore is { } notBefore)
                    chips.Add("From " + FormatMoment(notBefore, now));
                if (condition.WindowStart is { } start && condition.WindowEnd is { } end)
                    chips.Add(WindowText(start, end));
                break;
            case JobConditionKind.Unknown:
                chips.Add("Unknown condition");
                break;
        }

        if (condition.RequireIdle && condition.Kind != JobConditionKind.WhenIdle)
            chips.Add("When idle");
        if (condition.RequireCharging && condition.Kind != JobConditionKind.WhenCharging)
            chips.Add("When charging");
        return chips;
    }

    /// <summary><c>Tonight 01:00–06:00</c> for a night window, else <c>Daily 12:00–13:00</c>; equal ends = all day.</summary>
    public static string WindowText(TimeOnly start, TimeOnly end)
    {
        if (start == end)
            return "Any time of day";
        var range = $"{start.ToString("HH:mm", CultureInfo.InvariantCulture)}–{end.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        var night = start.Hour >= 18 || start.Hour < 6;
        return (night ? "Tonight " : "Daily ") + range;
    }

    /// <summary>Why a queued job is not running, in words; null when it runs next.</summary>
    public static string? WaitReasonText(HistoryJob job, JobWaitReason reason, DateTimeOffset now) => reason switch
    {
        JobWaitReason.None => null,
        JobWaitReason.NotQueued => job.State switch
        {
            JobState.Paused => "Paused",
            JobState.Unknown => "Unknown state · made by a newer VoiceType",
            _ => null,
        },
        JobWaitReason.UnknownKind => "Made by a newer VoiceType · not run by this version",
        JobWaitReason.AnotherJobRunning => "After the running job · one heavy job at a time",
        JobWaitReason.LiveRecording => "Waits until the live recording stops",
        JobWaitReason.NotCharging => "Waits for the charger",
        JobWaitReason.BeforeStartTime => job.Condition.NotBefore is { } at
            ? "Starts " + FormatMoment(at, now)
            : "Waits for its start time",
        JobWaitReason.OutsideWindow => job.Condition.WindowStart is { } s && job.Condition.WindowEnd is { } e
            ? "Waits for " + WindowText(s, e).Replace("Tonight ", "", StringComparison.Ordinal).Replace("Daily ", "", StringComparison.Ordinal)
            : "Waits for its time window",
        JobWaitReason.UnknownCondition => "Unknown condition · made by a newer VoiceType",
        JobWaitReason.WaitingForPreviousStep => "Waits for the previous step",
        _ => null,
    };

    /// <summary><c>42% · 5:16 of 12:34</c> (or just <c>42%</c> without a duration).</summary>
    public static string ProgressLine(double fraction, double? durationSec)
    {
        fraction = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        var percent = ((int)Math.Floor(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        if (durationSec is not > 0)
            return percent;
        return $"{percent} · {FormatClock(durationSec.Value * fraction)} of {FormatClock(durationSec.Value)}";
    }

    /// <summary>Remaining time from the elapsed time and the fraction done since the run started; null while unknown.</summary>
    public static TimeSpan? EstimateRemaining(TimeSpan elapsed, double fractionDoneThisRun, double fractionLeft)
    {
        if (fractionDoneThisRun < 0.01 || elapsed <= TimeSpan.Zero || fractionLeft <= 0)
            return fractionLeft <= 0 ? TimeSpan.Zero : null;
        return TimeSpan.FromSeconds(elapsed.TotalSeconds / fractionDoneThisRun * fractionLeft);
    }

    /// <summary><c>≈ 1 min left</c>, <c>&lt; 1 min left</c>, <c>≈ 1 h 5 min left</c>.</summary>
    public static string FormatEta(TimeSpan? remaining)
    {
        if (remaining is not { } r)
            return "estimating…";
        if (r < TimeSpan.FromMinutes(1))
            return "< 1 min left";
        return "≈ " + FormatSpan(r, withSeconds: false) + " left";
    }

    /// <summary>Processing time of re-recognizing <paramref name="audioSeconds"/> with a model of real-time factor <paramref name="rtf"/>.</summary>
    public static TimeSpan EstimateProcessing(double audioSeconds, double? rtf)
    {
        var factor = rtf is > 0 ? rtf.Value * StreamingOverhead : DefaultRealTimeFactor;
        return TimeSpan.FromSeconds(Math.Max(0, audioSeconds) * factor);
    }

    /// <summary><c>1 min 40 s</c>, <c>45 s</c>, <c>2 h 5 min</c>.</summary>
    public static string FormatSpan(TimeSpan span, bool withSeconds = true)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        var totalSeconds = (long)Math.Round(span.TotalSeconds);
        long hours = totalSeconds / 3600, minutes = totalSeconds % 3600 / 60, seconds = totalSeconds % 60;
        if (hours > 0)
            return minutes > 0 ? $"{hours} h {minutes} min" : $"{hours} h";
        if (minutes > 0)
        {
            if (!withSeconds || seconds == 0)
                return $"{Math.Max(1, (long)Math.Round(span.TotalMinutes))} min";
            return $"{minutes} min {seconds} s";
        }
        return withSeconds ? $"{seconds} s" : "1 min";
    }

    /// <summary><c>5:16</c> or <c>1:02:03</c>.</summary>
    public static string FormatClock(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0
            ? $"{h}:{m:00}:{s:00}"
            : $"{m}:{s:00}";
    }

    /// <summary><c>14:00</c> today, <c>tomorrow 01:00</c>, else <c>Mon 12 Oct 14:00</c>.</summary>
    public static string FormatMoment(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = moment.ToOffset(now.Offset);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == now.Date)
            return time;
        if (local.Date == now.Date.AddDays(1))
            return "tomorrow " + time;
        return local.ToString("ddd d MMM ", CultureInfo.InvariantCulture) + time;
    }
}
