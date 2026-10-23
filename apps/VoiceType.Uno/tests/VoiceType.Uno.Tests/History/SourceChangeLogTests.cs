using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class SourceChangeLogTests
{
    [Fact]
    public void Record_ShouldKeepEachSwitchAtItsPosition()
    {
        var log = new SourceChangeLog("Mic");

        log.Record(48_000, "Loopback");
        log.Record(112_000, "Mix");

        Assert.Equal([(48_000L, "Loopback"), (112_000L, "Mix")], log.Changes);
    }

    [Fact]
    public void Record_ASwitchToTheSourceInUse_ShouldBeIgnored()
    {
        var log = new SourceChangeLog("Mic");

        log.Record(16_000, "mic");

        Assert.Empty(log.Changes);
    }

    [Fact]
    public void Record_ASourceThatFailedAndTheOneRestoredAtOnce_ShouldCancelOut()
    {
        var log = new SourceChangeLog("Mic");

        log.Record(48_000, "Loopback");   // tried
        log.Record(48_000, "Mic");        // did not start: the microphone is back, no audio in between

        Assert.Empty(log.Changes);
    }

    [Fact]
    public void Record_TwoSwitchesWithoutAudioBetween_ShouldKeepTheLastOne()
    {
        var log = new SourceChangeLog("Mic");

        log.Record(48_000, "Loopback");
        log.Record(48_000, "Mix");

        Assert.Equal([(48_000L, "Mix")], log.Changes);
    }

    [Fact]
    public void Record_ASwitchBackAfterAudio_ShouldBeKept()
    {
        var log = new SourceChangeLog("Mic");

        log.Record(48_000, "Loopback");
        log.Record(64_000, "Mic");

        Assert.Equal([(48_000L, "Loopback"), (64_000L, "Mic")], log.Changes);
    }
}
