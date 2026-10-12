using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class ReprocessPlanTests
{
    private static TranscriptVersion Version(string id, string kind, string? folder, double? rtf = null) => new()
    {
        Id = id, Kind = kind, Model = folder is null ? null : new ModelInfo { Folder = folder },
        Stats = new VersionStats { Rtf = rtf },
    };

    private static RecordingManifest Recording(double seconds, params TranscriptVersion[] versions) => new()
    {
        Audio = new AudioInfo { File = "audio.ogg", DurationSec = seconds },
        Versions = versions.ToList(),
    };

    [Fact]
    public void UsedText_ShouldListVersionsForOneRecordingAndCountForMany()
    {
        var a = Recording(60, Version("v1", VersionKinds.Live, "p-int4"), Version("v2", VersionKinds.Rerecognized, "p-fp32"),
            Version("v3", VersionKinds.Edited, "p-int4"), Version("v4", VersionKinds.Rerecognized, "p-int4"));
        var b = Recording(30, Version("v1", VersionKinds.Live, "nemotron"));

        Assert.Equal("Used for v1, v4", ReprocessPlan.UsedText("p-int4", [a]));
        Assert.Null(ReprocessPlan.UsedText("nemotron", [a]));
        Assert.Equal("Used in 1 of 2", ReprocessPlan.UsedText("nemotron", [a, b]));
        Assert.Null(ReprocessPlan.UsedText("other", [a, b]));
    }

    [Fact]
    public void Estimate_ShouldPreferTheSpeedMeasuredOnThisDevice()
    {
        var a = Recording(100, Version("v2", VersionKinds.Rerecognized, "p", rtf: 0.6), Version("v3", VersionKinds.Rerecognized, "p", rtf: 0.2),
            Version("v4", VersionKinds.Rerecognized, "p", rtf: 0.4));
        var noAudio = new RecordingManifest { Audio = new AudioInfo { File = "audio.ogg", DurationSec = 50, RemovedAt = DateTimeOffset.Now } };

        var measured = ReprocessPlan.MeasuredRealTimeFactor("p", [a]);

        Assert.Equal(0.4, measured);
        Assert.Null(ReprocessPlan.MeasuredRealTimeFactor("q", [a]));
        Assert.Equal(TimeSpan.FromSeconds(40), ReprocessPlan.Estimate([a, noAudio], measured, catalogRtf: 0.1));
        Assert.Equal(JobDisplay.EstimateProcessing(100, 0.1), ReprocessPlan.Estimate([a], null, 0.1));
    }

    [Fact]
    public void NextVersionId_ShouldFollowTheHighestId() =>
        Assert.Equal("v5", ReprocessPlan.NextVersionId(Recording(1, Version("v1", VersionKinds.Live, null), Version("v4", VersionKinds.Edited, null))));
}
