using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryUiPendingDeletesTests
{
    private readonly ManualTimeProvider _clock = new(HistoryFixtures.T0);

    [Fact]
    public void Schedule_ShouldHideItemsUntilCommitted()
    {
        var pending = new HistoryUiPendingDeletes(_clock);
        var committed = 0;

        pending.Schedule("Recording deleted", ["a"], [HistoryUiPendingDeletes.VersionKey("b", "v1")], () => committed++);

        Assert.True(pending.IsRecordingHidden("a"));
        Assert.False(pending.IsRecordingHidden("b"));
        Assert.True(pending.IsVersionHidden("b", "v1"));
        Assert.Empty(pending.TakeDue());

        _clock.Advance(HistoryUiPendingDeletes.UndoWindow);
        foreach (var commit in pending.TakeDue())
            commit();

        Assert.Equal(1, committed);
        Assert.False(pending.IsRecordingHidden("a"));
        Assert.Null(pending.Latest);
        Assert.Null(pending.NextDueIn);
    }

    [Fact]
    public void Undo_ShouldDropTheDeleteWithoutCommitting()
    {
        var pending = new HistoryUiPendingDeletes(_clock);
        var changes = 0;
        pending.Changed += () => changes++;
        var entry = pending.Schedule("x", ["a"], null, () => throw new InvalidOperationException("must not run"));

        Assert.True(pending.Undo(entry.Token));
        Assert.False(pending.Undo(entry.Token));
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(pending.TakeDue());
        Assert.False(pending.IsRecordingHidden("a"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Take_ShouldCommitOneEntryEarly()
    {
        var pending = new HistoryUiPendingDeletes(_clock);
        var first = pending.Schedule("first", ["a"], null, () => { });
        _clock.Advance(TimeSpan.FromSeconds(3));
        var second = pending.Schedule("second", ["b"], null, () => { });

        Assert.Equal(second, pending.Latest);
        Assert.Equal(TimeSpan.FromSeconds(5), pending.NextDueIn);
        Assert.NotNull(pending.Take(second.Token));
        Assert.Equal(first, pending.Latest);
        Assert.Single(pending.TakeDue(all: true));
    }
}
