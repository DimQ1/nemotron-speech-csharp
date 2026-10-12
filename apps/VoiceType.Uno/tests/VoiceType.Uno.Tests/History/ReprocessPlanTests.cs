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
    public void DeviceSlowdown_ShouldUseTheMedianRatioOfMeasuredToCatalogSpeed()
    {
        // catalog 0.1 x 1.25 overhead = 0.125 expected; measured 0.5 -> 4x slower, measured 0.375 -> 3x.
        var a = Recording(100, Version("v2", VersionKinds.Rerecognized, "p", rtf: 0.5), Version("v3", VersionKinds.Rerecognized, "p", rtf: 0.375),
            Version("v4", VersionKinds.Rerecognized, "p", rtf: 0.625));

        var slowdown = ReprocessPlan.DeviceSlowdown([a], folder => folder == "p" ? 0.1 : null, fallback: 9);

        Assert.Equal(4.0, slowdown, 6);
    }

    [Fact]
    public void DeviceSlowdown_ShouldFallBackWhenNothingWasMeasuredHere()
    {
        var live = Recording(60, Version("v1", VersionKinds.Live, "p"));

        Assert.Equal(4.0, ReprocessPlan.DeviceSlowdown([live], _ => 0.1, ReprocessPlan.PhoneSlowdown));
        Assert.Equal(1.0, ReprocessPlan.DeviceSlowdown([], _ => 0.1, 1.0));
    }

    [Fact]
    public void DeviceSlowdown_ShouldNeverPromiseFasterThanTheCatalog()
    {
        var a = Recording(60, Version("v2", VersionKinds.Rerecognized, "p", rtf: 0.05));

        Assert.Equal(1.0, ReprocessPlan.DeviceSlowdown([a], _ => 0.1, 7.0));
    }

    [Fact]
    public void Estimate_ShouldScaleTheCatalogSpeedByTheDeviceSlowdownOnlyWhenNothingWasMeasured()
    {
        var a = Recording(100);

        var baseline = ReprocessPlan.Estimate([a], null, 0.1).TotalSeconds;

        Assert.Equal(baseline * 4, ReprocessPlan.Estimate([a], null, 0.1, deviceSlowdown: 4).TotalSeconds, 6);
        Assert.Equal(TimeSpan.FromSeconds(60), ReprocessPlan.Estimate([a], 0.6, 0.1, deviceSlowdown: 4));
        // no catalog speed -> the generic default is used as is
        Assert.Equal(ReprocessPlan.Estimate([a], null, null).TotalSeconds, ReprocessPlan.Estimate([a], null, null, 4).TotalSeconds);
    }

    [Theory]
    [InlineData("ParakeetTdt", false)]
    [InlineData("parakeettdt", false)]
    [InlineData("Nemotron", true)]
    [InlineData("Qwen3", true)]
    [InlineData(null, true)]
    public void SupportsLanguageChoice_ShouldBeFalseOnlyForParakeet(string? family, bool expected) =>
        Assert.Equal(expected, ReprocessPlan.SupportsLanguageChoice(family));

    [Fact]
    public void NextVersionId_ShouldFollowTheHighestId() =>
        Assert.Equal("v5", ReprocessPlan.NextVersionId(Recording(1, Version("v1", VersionKinds.Live, null), Version("v4", VersionKinds.Edited, null))));
}
