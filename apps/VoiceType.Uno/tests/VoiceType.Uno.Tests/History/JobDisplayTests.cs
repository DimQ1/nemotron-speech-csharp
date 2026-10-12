using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class JobDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConditionChips_ShouldDescribeEveryConditionPart()
    {
        var window = JobCondition.Window(new(1, 0), new(6, 0));
        window.RequireCharging = true;

        Assert.Empty(JobDisplay.ConditionChips(JobCondition.Now(), Now));
        Assert.Equal(["When idle"], JobDisplay.ConditionChips(JobCondition.WhenIdle(), Now));
        Assert.Equal(["When charging"], JobDisplay.ConditionChips(JobCondition.WhenCharging(), Now));
        Assert.Equal(["Tonight 01:00–06:00", "When charging"], JobDisplay.ConditionChips(window, Now));
        Assert.Equal(["From tomorrow 09:30"], JobDisplay.ConditionChips(JobCondition.At(Now.AddHours(19.5)), Now));
        Assert.Equal(["Daily 12:00–13:00"], JobDisplay.ConditionChips(JobCondition.Window(new(12, 0), new(13, 0)), Now));
    }

    [Fact]
    public void WaitReasonText_ShouldExplainWhyAJobWaits()
    {
        var job = new HistoryJob { Condition = JobCondition.Window(new(1, 0), new(6, 0)) };

        Assert.Null(JobDisplay.WaitReasonText(job, JobWaitReason.None, Now));
        Assert.Equal("Waits for 01:00–06:00", JobDisplay.WaitReasonText(job, JobWaitReason.OutsideWindow, Now));
        Assert.Equal("Waits for the charger", JobDisplay.WaitReasonText(job, JobWaitReason.NotCharging, Now));
        Assert.Equal("Paused", JobDisplay.WaitReasonText(new HistoryJob { State = JobState.Paused }, JobWaitReason.NotQueued, Now));
        Assert.Contains("newer VoiceType", JobDisplay.WaitReasonText(job, JobWaitReason.UnknownKind, Now));
    }

    [Theory]
    [InlineData(0.42, 754.0, "42% · 5:16 of 12:34")]
    [InlineData(0.0, null, "0%")]
    [InlineData(1.0, 3723.0, "100% · 1:02:03 of 1:02:03")]
    public void ProgressLine_ShouldShowPercentAndAudioPosition(double fraction, double? duration, string expected) =>
        Assert.Equal(expected, JobDisplay.ProgressLine(fraction, duration));

    [Fact]
    public void Eta_ShouldExtrapolateFromThisRun()
    {
        var remaining = JobDisplay.EstimateRemaining(TimeSpan.FromSeconds(60), fractionDoneThisRun: 0.25, fractionLeft: 0.5);

        Assert.Equal(TimeSpan.FromSeconds(120), remaining);
        Assert.Equal("≈ 2 min left", JobDisplay.FormatEta(remaining));
        Assert.Equal("< 1 min left", JobDisplay.FormatEta(TimeSpan.FromSeconds(20)));
        Assert.Equal("estimating…", JobDisplay.FormatEta(JobDisplay.EstimateRemaining(TimeSpan.FromSeconds(1), 0.001, 0.9)));
    }

    [Fact]
    public void EstimateProcessing_ShouldScaleCatalogSpeedByStreamingOverhead()
    {
        Assert.Equal(TimeSpan.FromSeconds(100 * 0.2 * JobDisplay.StreamingOverhead), JobDisplay.EstimateProcessing(100, 0.2));
        Assert.Equal(TimeSpan.FromSeconds(100 * JobDisplay.DefaultRealTimeFactor), JobDisplay.EstimateProcessing(100, null));
        Assert.Equal("1 min 40 s", JobDisplay.FormatSpan(TimeSpan.FromSeconds(100)));
        Assert.Equal("45 s", JobDisplay.FormatSpan(TimeSpan.FromSeconds(45)));
        Assert.Equal("2 h 5 min", JobDisplay.FormatSpan(TimeSpan.FromMinutes(125)));
    }
}
