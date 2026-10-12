using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class JobSchedulerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static HistoryJob Job(string id, int priority = 0, JobCondition? condition = null,
        JobState state = JobState.Queued, HistoryJobKind kind = HistoryJobKind.Rerecognize) => new()
    {
        Id = id, Kind = kind, RecordingId = "r" + id, Priority = priority,
        Condition = condition ?? JobCondition.Now(), State = state,
    };

    private static SchedulerContext Ctx(DateTimeOffset? now = null, bool recording = false, bool charging = false,
        bool android = false, bool allowDuringRecording = true) =>
        new(now ?? Noon, recording, charging, android, allowDuringRecording);

    [Fact]
    public void SelectNext_ShouldReturnNullForEmptyQueue() =>
        Assert.Null(JobScheduler.SelectNext([], Ctx()));

    [Fact]
    public void SelectNext_ShouldPickHigherPriorityThenFifo()
    {
        HistoryJob[] queue = [Job("a"), Job("b", priority: 10), Job("c", priority: 10), Job("d", priority: -10)];

        Assert.Equal("b", JobScheduler.SelectNext(queue, Ctx())!.Id);
    }

    [Fact]
    public void SelectNext_ShouldKeepQueueOrderWithinPriority()
    {
        HistoryJob[] queue = [Job("x"), Job("y")];

        Assert.Equal("x", JobScheduler.SelectNext(queue, Ctx())!.Id);
    }

    [Fact]
    public void SelectNext_ShouldRunOneHeavyJobAtATime()
    {
        HistoryJob[] queue = [Job("a", state: JobState.Running), Job("b", priority: 100)];

        Assert.Null(JobScheduler.SelectNext(queue, Ctx()));
        Assert.Equal(JobWaitReason.AnotherJobRunning, JobScheduler.GetWaitReason(queue[1], queue, Ctx()));
        Assert.Equal(JobState.Queued, JobScheduler.GetDisplayState(queue[1], queue, Ctx()));
    }

    [Theory]
    [InlineData(JobState.Paused)]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.Unknown)]
    public void SelectNext_ShouldSkipJobsThatAreNotQueued(JobState state)
    {
        HistoryJob[] queue = [Job("a", state: state), Job("b", priority: -5)];

        Assert.Equal("b", JobScheduler.SelectNext(queue, Ctx())!.Id);
    }

    [Fact]
    public void SelectNext_ShouldSkipUnknownKinds()
    {
        HistoryJob[] queue = [Job("a", kind: HistoryJobKind.Unknown)];

        Assert.Null(JobScheduler.SelectNext(queue, Ctx()));
    }

    // condition, recording, charging, android, allowDuringRecording, expected
    [Theory]
    [InlineData(JobConditionKind.Now, false, false, false, true, JobWaitReason.None)]
    [InlineData(JobConditionKind.Now, true, false, false, true, JobWaitReason.None)]
    [InlineData(JobConditionKind.Now, true, false, false, false, JobWaitReason.LiveRecording)]
    [InlineData(JobConditionKind.WhenIdle, false, false, false, true, JobWaitReason.None)]
    [InlineData(JobConditionKind.WhenIdle, true, false, false, true, JobWaitReason.LiveRecording)]
    [InlineData(JobConditionKind.WhenCharging, false, false, true, true, JobWaitReason.NotCharging)]
    [InlineData(JobConditionKind.WhenCharging, false, true, true, true, JobWaitReason.None)]
    [InlineData(JobConditionKind.WhenCharging, false, false, false, true, JobWaitReason.None)]
    [InlineData(JobConditionKind.WhenCharging, true, true, true, false, JobWaitReason.LiveRecording)]
    [InlineData(JobConditionKind.Unknown, false, false, false, true, JobWaitReason.UnknownCondition)]
    public void GetWaitReason_ShouldFollowTheConditionTable(
        JobConditionKind condition, bool recording, bool charging, bool android, bool allow, JobWaitReason expected)
    {
        var job = Job("a", condition: new JobCondition { Kind = condition });
        var ctx = Ctx(recording: recording, charging: charging, android: android, allowDuringRecording: allow);

        Assert.Equal(expected, JobScheduler.GetWaitReason(job, [job], ctx));
        Assert.Equal(expected == JobWaitReason.None ? job : null, JobScheduler.SelectNext([job], ctx));
    }

    [Fact]
    public void SelectNext_ShouldSkipWaitingJobAndTakeTheNextEligible()
    {
        HistoryJob[] queue = [Job("idle", priority: 5, condition: JobCondition.WhenIdle()), Job("now")];
        var ctx = Ctx(recording: true);

        Assert.Equal("now", JobScheduler.SelectNext(queue, ctx)!.Id);
        Assert.Equal(JobState.Waiting, JobScheduler.GetDisplayState(queue[0], queue, ctx));
    }

    [Fact]
    public void GetWaitReason_ShouldWaitForNotBefore()
    {
        var job = Job("a", condition: JobCondition.At(Noon.AddHours(1)));

        Assert.Equal(JobWaitReason.BeforeStartTime, JobScheduler.GetWaitReason(job, [job], Ctx(Noon)));
        Assert.Equal(JobWaitReason.None, JobScheduler.GetWaitReason(job, [job], Ctx(Noon.AddHours(1))));
    }

    // window 22:00–06:00 crosses midnight
    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    [InlineData(12, 0, false)]
    public void AtTimeWindow_ShouldHandleWindowsCrossingMidnight(int hour, int minute, bool eligible)
    {
        var job = Job("a", condition: JobCondition.Window(new TimeOnly(22, 0), new TimeOnly(6, 0)));
        var now = new DateTimeOffset(2026, 10, 12, hour, minute, 0, TimeSpan.FromHours(3));

        var reason = JobScheduler.GetWaitReason(job, [job], Ctx(now));

        Assert.Equal(eligible ? JobWaitReason.None : JobWaitReason.OutsideWindow, reason);
    }

    [Theory]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]
    public void IsInWindow_ShouldTreatSameDayWindowAsHalfOpen(int hour, int minute, bool expected) =>
        Assert.Equal(expected, JobScheduler.IsInWindow(new TimeOnly(hour, minute), new TimeOnly(9, 0), new TimeOnly(17, 0)));

    [Fact]
    public void IsInWindow_ShouldTreatEqualBoundsAsAllDay() =>
        Assert.True(JobScheduler.IsInWindow(new TimeOnly(3, 0), new TimeOnly(9, 0), new TimeOnly(9, 0)));

    [Fact]
    public void AtTime_ShouldCombineNotBeforeAndWindow()
    {
        var condition = JobCondition.Window(new TimeOnly(22, 0), new TimeOnly(6, 0));
        condition.NotBefore = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        var job = Job("a", condition: condition);

        Assert.Equal(JobWaitReason.BeforeStartTime,
            JobScheduler.GetWaitReason(job, [job], Ctx(new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.Zero))));
        Assert.Equal(JobWaitReason.None,
            JobScheduler.GetWaitReason(job, [job], Ctx(new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero))));
        Assert.Equal(JobWaitReason.OutsideWindow,
            JobScheduler.GetWaitReason(job, [job], Ctx(new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero))));
    }
}
