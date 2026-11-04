using System.Globalization;
using VoiceType.Uno.Localization;

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
        HistoryJobKind.Rerecognize => Loc.T("Queue.Kind.Rerecognize"),
        HistoryJobKind.Translate => Loc.T("Queue.Kind.Translate"),
        HistoryJobKind.AutoTitle => Loc.T("Queue.Kind.AutoTitle"),
        HistoryJobKind.Summarize => Loc.T("Queue.Kind.Summarize"),
        HistoryJobKind.Diarize => Loc.T("Queue.Kind.Diarize"),
        HistoryJobKind.Analyze => Loc.T("Queue.Kind.Analyze"),
        _ => Loc.T("Queue.Kind.Unknown"),
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
                chips.Add(Loc.T("Queue.Condition.WhenIdle"));
                break;
            case JobConditionKind.WhenCharging:
                chips.Add(Loc.T("Queue.Condition.WhenCharging"));
                break;
            case JobConditionKind.AtTime:
                if (condition.NotBefore is { } notBefore)
                    chips.Add(Loc.F("Queue.Condition.From", FormatMoment(notBefore, now)));
                if (condition.WindowStart is { } start && condition.WindowEnd is { } end)
                    chips.Add(WindowText(start, end));
                break;
            case JobConditionKind.Unknown:
                chips.Add(Loc.T("Queue.Condition.Unknown"));
                break;
        }

        if (condition.RequireIdle && condition.Kind != JobConditionKind.WhenIdle)
            chips.Add(Loc.T("Queue.Condition.WhenIdle"));
        if (condition.RequireCharging && condition.Kind != JobConditionKind.WhenCharging)
            chips.Add(Loc.T("Queue.Condition.WhenCharging"));
        return chips;
    }

    /// <summary><c>Tonight 01:00–06:00</c> for a night window, else <c>Daily 12:00–13:00</c>; equal ends = all day.</summary>
    public static string WindowText(TimeOnly start, TimeOnly end)
    {
        if (start == end)
            return Loc.T("Queue.Window.AnyTime");
        var night = start.Hour >= 18 || start.Hour < 6;
        return Loc.F(night ? "Queue.Window.Tonight" : "Queue.Window.Daily", RangeText(start, end));
    }

    /// <summary><c>01:00–06:00</c>.</summary>
    private static string RangeText(TimeOnly start, TimeOnly end) =>
        start.ToString("HH:mm", Loc.Culture) + "–" + end.ToString("HH:mm", Loc.Culture);

    /// <summary>Why a queued job is not running, in words; null when it runs next.</summary>
    public static string? WaitReasonText(HistoryJob job, JobWaitReason reason, DateTimeOffset now) => reason switch
    {
        JobWaitReason.None => null,
        JobWaitReason.NotQueued => job.State switch
        {
            JobState.Paused => Loc.T("Queue.Wait.Paused"),
            JobState.Unknown => Loc.T("Queue.Wait.UnknownState"),
            _ => null,
        },
        JobWaitReason.UnknownKind => Loc.T("Queue.Wait.UnknownKind"),
        JobWaitReason.AnotherJobRunning => Loc.T("Queue.Wait.AnotherJob"),
        JobWaitReason.LiveRecording => Loc.T("Queue.Wait.LiveRecording"),
        JobWaitReason.NotCharging => Loc.T("Queue.Wait.Charger"),
        JobWaitReason.BeforeStartTime => job.Condition.NotBefore is { } at
            ? Loc.F("Queue.Wait.StartsAt", FormatMoment(at, now))
            : Loc.T("Queue.Wait.StartTime"),
        JobWaitReason.OutsideWindow => job.Condition.WindowStart is { } s && job.Condition.WindowEnd is { } e
            ? (s == e ? Loc.F("Queue.Wait.ForWindow", Loc.T("Queue.Window.AnyTime")) : Loc.F("Queue.Wait.ForWindow", RangeText(s, e)))
            : Loc.T("Queue.Wait.Window"),
        JobWaitReason.UnknownCondition => Loc.T("Queue.Wait.UnknownCondition"),
        JobWaitReason.WaitingForPreviousStep => Loc.T("Queue.Wait.PreviousStep"),
        _ => null,
    };

    /// <summary><c>42% · 5:16 of 12:34</c> (or just <c>42%</c> without a duration).</summary>
    public static string ProgressLine(double fraction, double? durationSec)
    {
        fraction = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        var percent = (int)Math.Floor(fraction * 100);
        if (durationSec is not > 0)
            return Loc.F("Queue.Progress.Percent", percent);
        return Loc.F("Queue.Progress.PercentOf", percent, FormatClock(durationSec.Value * fraction), FormatClock(durationSec.Value));
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
            return Loc.T("Queue.Eta.Estimating");
        if (r < TimeSpan.FromMinutes(1))
            return Loc.T("Queue.Eta.UnderMinute");
        return Loc.F("Queue.Eta.Left", FormatSpan(r, withSeconds: false));
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
            return minutes > 0 ? Loc.F("Queue.Span.HoursMinutes", hours, minutes) : Loc.F("Queue.Span.Hours", hours);
        if (minutes > 0)
        {
            if (!withSeconds || seconds == 0)
                return Loc.F("Queue.Span.Minutes", Math.Max(1, (long)Math.Round(span.TotalMinutes)));
            return Loc.F("Queue.Span.MinutesSeconds", minutes, seconds);
        }
        return withSeconds ? Loc.F("Queue.Span.Seconds", seconds) : Loc.F("Queue.Span.Minutes", 1L);
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
        var time = local.ToString("HH:mm", Loc.Culture);
        if (local.Date == now.Date)
            return time;
        if (local.Date == now.Date.AddDays(1))
            return Loc.F("Queue.Moment.Tomorrow", time);
        return Loc.F("Queue.Moment.Date", local.ToString("ddd d MMM", Loc.Culture), time);
    }
}
