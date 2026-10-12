namespace VoiceType.Uno.History;

/// <summary>Device/app state the scheduler decides on.</summary>
/// <param name="Now">Local time; daily windows use its clock time.</param>
/// <param name="IsLiveRecording">A live recording is running.</param>
/// <param name="IsCharging">Phone is on the charger (ignored on desktop).</param>
/// <param name="IsAndroid">Battery-powered platform where <see cref="JobConditionKind.WhenCharging"/> matters.</param>
/// <param name="AllowDuringRecording">Setting: heavy jobs may run next to a live recording.</param>
public sealed record SchedulerContext(
    DateTimeOffset Now,
    bool IsLiveRecording = false,
    bool IsCharging = false,
    bool IsAndroid = false,
    bool AllowDuringRecording = true);

public enum JobWaitReason
{
    /// <summary>Can start now.</summary>
    None,
    /// <summary>Not in <see cref="JobState.Queued"/> (paused, running, finished).</summary>
    NotQueued,
    /// <summary>Kind unknown to this app version.</summary>
    UnknownKind,
    /// <summary>Another heavy job is running (one at a time).</summary>
    AnotherJobRunning,
    /// <summary>A live recording runs and the job/setting waits for idle.</summary>
    LiveRecording,
    NotCharging,
    BeforeStartTime,
    OutsideWindow,
    UnknownCondition,
    /// <summary>Schema 2: <see cref="HistoryJob.DependsOn"/> has not completed yet.</summary>
    WaitingForPreviousStep,
}

/// <summary>
/// Pure scheduling rules for the history job queue: one heavy job at a time,
/// higher priority first, FIFO (queue order) within a priority, and per-job start
/// conditions (now / idle / charging / time window).
/// </summary>
public static class JobScheduler
{
    /// <summary>Every current job kind loads a recognizer or Gemma, so all are heavy.</summary>
    public static bool IsHeavy(HistoryJobKind kind) => kind != HistoryJobKind.Unknown;

    /// <summary>The job to start next, or null when nothing may start now.</summary>
    public static HistoryJob? SelectNext(IReadOnlyList<HistoryJob> queue, SchedulerContext context)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(context);

        HistoryJob? best = null;
        for (var i = 0; i < queue.Count; i++)
        {
            var job = queue[i];
            if (GetWaitReason(job, queue, context) != JobWaitReason.None)
                continue;
            // Strictly greater: the earlier job wins a priority tie (FIFO).
            if (best is null || job.Priority > best.Priority)
                best = job;
        }
        return best;
    }

    /// <summary>Why <paramref name="job"/> cannot start now (<see cref="JobWaitReason.None"/> if it can).</summary>
    public static JobWaitReason GetWaitReason(HistoryJob job, IReadOnlyList<HistoryJob> queue, SchedulerContext context)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State != JobState.Queued)
            return JobWaitReason.NotQueued;
        if (job.Kind == HistoryJobKind.Unknown)
            return JobWaitReason.UnknownKind;
        if (job.DependsOn is { } dependsOn
            && queue.FirstOrDefault(j => j.Id == dependsOn) is { State: not JobState.Completed })
            return JobWaitReason.WaitingForPreviousStep;
        if (IsHeavy(job.Kind) && queue.Any(j => j.State == JobState.Running && IsHeavy(j.Kind)))
            return JobWaitReason.AnotherJobRunning;
        if (context.IsLiveRecording && !context.AllowDuringRecording)
            return JobWaitReason.LiveRecording;
        return GetConditionReason(job.Condition, context);
    }

    /// <summary>Whether the job's own start condition holds (ignores other jobs).</summary>
    public static JobWaitReason GetConditionReason(JobCondition? condition, SchedulerContext context)
    {
        condition ??= JobCondition.Now();
        var reason = GetKindReason(condition, context);
        if (reason != JobWaitReason.None)
            return reason;
        // Schema 2: extra requirements on top of the kind (a conjunction).
        if (condition.RequireIdle && context.IsLiveRecording)
            return JobWaitReason.LiveRecording;
        if (condition.RequireCharging && context.IsAndroid && !context.IsCharging)
            return JobWaitReason.NotCharging;
        return JobWaitReason.None;
    }

    private static JobWaitReason GetKindReason(JobCondition condition, SchedulerContext context)
    {
        switch (condition.Kind)
        {
            case JobConditionKind.Now:
                return JobWaitReason.None;
            case JobConditionKind.WhenIdle:
                return context.IsLiveRecording ? JobWaitReason.LiveRecording : JobWaitReason.None;
            case JobConditionKind.WhenCharging:
                return context.IsAndroid && !context.IsCharging ? JobWaitReason.NotCharging : JobWaitReason.None;
            case JobConditionKind.AtTime:
                if (condition.NotBefore is { } notBefore && context.Now < notBefore)
                    return JobWaitReason.BeforeStartTime;
                if (condition.WindowStart is { } start && condition.WindowEnd is { } end
                    && !IsInWindow(TimeOnly.FromTimeSpan(context.Now.TimeOfDay), start, end))
                    return JobWaitReason.OutsideWindow;
                return JobWaitReason.None;
            default:
                return JobWaitReason.UnknownCondition;
        }
    }

    /// <summary>State to show: a queued job whose condition is not met reads as <see cref="JobState.Waiting"/>.</summary>
    public static JobState GetDisplayState(HistoryJob job, IReadOnlyList<HistoryJob> queue, SchedulerContext context)
    {
        if (job.State != JobState.Queued)
            return job.State;
        return GetWaitReason(job, queue, context) switch
        {
            JobWaitReason.None or JobWaitReason.AnotherJobRunning => JobState.Queued,
            _ => JobState.Waiting,
        };
    }

    /// <summary>
    /// [start, end) on the daily clock; a window with start &gt; end crosses midnight
    /// (22:00–06:00); start == end means all day.
    /// </summary>
    public static bool IsInWindow(TimeOnly time, TimeOnly start, TimeOnly end)
    {
        if (start == end)
            return true;
        return start < end
            ? time >= start && time < end
            : time >= start || time < end;
    }
}
