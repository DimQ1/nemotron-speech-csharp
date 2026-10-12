using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>Schema-2 scheduler rules: dependsOn and conditions combined with requireCharging/requireIdle.</summary>
public class JobSchedulerChainTests
{
    private static readonly DateTimeOffset Night = new(2026, 10, 12, 2, 0, 0, TimeSpan.Zero);

    private static HistoryJob Job(string id, JobState state = JobState.Queued, string? dependsOn = null, JobCondition? condition = null) => new()
    {
        Id = id, Kind = HistoryJobKind.Rerecognize, RecordingId = "r", State = state,
        DependsOn = dependsOn, Condition = condition ?? JobCondition.Now(),
    };

    [Fact]
    public void GetWaitReason_ShouldWaitForUnfinishedDependency()
    {
        HistoryJob[] queue = [Job("a", JobState.Paused), Job("b", dependsOn: "a")];

        Assert.Equal(JobWaitReason.WaitingForPreviousStep, JobScheduler.GetWaitReason(queue[1], queue, new(Night)));
        Assert.Null(JobScheduler.SelectNext(queue, new(Night)));
    }

    [Fact]
    public void GetWaitReason_ShouldStartAfterDependencyCompletedOrRemoved()
    {
        HistoryJob[] done = [Job("a", JobState.Completed), Job("b", dependsOn: "a")];
        HistoryJob[] removed = [Job("b", dependsOn: "gone")];

        Assert.Equal("b", JobScheduler.SelectNext(done, new(Night))!.Id);
        Assert.Equal("b", JobScheduler.SelectNext(removed, new(Night))!.Id);
    }

    [Fact]
    public void GetConditionReason_ShouldCombineWindowWithRequireCharging()
    {
        var condition = JobCondition.Window(new(1, 0), new(6, 0));
        condition.RequireCharging = true;

        Assert.Equal(JobWaitReason.NotCharging, JobScheduler.GetConditionReason(condition, new(Night, IsAndroid: true)));
        Assert.Equal(JobWaitReason.None, JobScheduler.GetConditionReason(condition, new(Night, IsCharging: true, IsAndroid: true)));
        Assert.Equal(JobWaitReason.OutsideWindow, JobScheduler.GetConditionReason(condition, new(Night.AddHours(10), IsCharging: true, IsAndroid: true)));
        // Desktop without battery: charging never blocks.
        Assert.Equal(JobWaitReason.None, JobScheduler.GetConditionReason(condition, new(Night)));
    }

    [Fact]
    public void GetConditionReason_ShouldHonourRequireIdle()
    {
        var condition = JobCondition.WhenCharging();
        condition.RequireIdle = true;

        Assert.Equal(JobWaitReason.LiveRecording, JobScheduler.GetConditionReason(condition, new(Night, IsLiveRecording: true, IsCharging: true, IsAndroid: true)));
        Assert.Equal(JobWaitReason.None, JobScheduler.GetConditionReason(condition, new(Night, IsCharging: true, IsAndroid: true)));
    }
}
