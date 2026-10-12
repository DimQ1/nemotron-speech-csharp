using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryUiDiffTests
{
    [Fact]
    public void Build_ShouldMarkReplacedWordsAsDeletedThenInserted()
    {
        var a = HistoryFixtures.Transcript((0, 5, "The meeting moved to ten"), (5, 9, "over in cloud cost"));
        var b = HistoryFixtures.Transcript((0, 5, "The meeting moves to ten"), (5, 9, "over in cloud costs"));

        var view = HistoryUiDiff.Build(a, b);

        Assert.False(view.IsIdentical);
        Assert.Equal(2, view.Segments.Count);
        Assert.Equal(
            [(HistoryDiffRunKind.Same, "The meeting"), (HistoryDiffRunKind.Deleted, "moved"), (HistoryDiffRunKind.Inserted, "moves"), (HistoryDiffRunKind.Same, "to ten")],
            view.Segments[0].Runs.Select(r => (r.Kind, r.Text)));
        Assert.Equal("00:05", view.Segments[1].TimeText);
        Assert.Equal("2 words changed in 2 of 2 segments · 22% of words", view.StatsText);
    }

    [Fact]
    public void Build_ShouldReportIdenticalVersionsAndNoChangedSegments()
    {
        var a = HistoryFixtures.Transcript((0, 5, "Same text here"));
        var view = HistoryUiDiff.Build(a, HistoryFixtures.Transcript((0, 5, "same text, here")));

        Assert.True(view.IsIdentical);
        Assert.Empty(view.ChangedSegments);
        Assert.Equal("No word changes", view.StatsText);
    }

    [Fact]
    public void Build_ShouldAttachPureDeletionToThePreviousWordSegment()
    {
        var a = HistoryFixtures.Transcript((0, 5, "one two three"), (5, 9, "four five"));
        var b = HistoryFixtures.Transcript((0, 5, "one two"), (5, 9, "four five"));

        var view = HistoryUiDiff.Build(a, b);

        Assert.Single(view.ChangedSegments);
        Assert.Equal((HistoryDiffRunKind.Deleted, "three"), (view.Segments[0].Runs[^1].Kind, view.Segments[0].Runs[^1].Text));
        Assert.False(view.Segments[1].HasChanges);
    }

    [Fact]
    public void Build_ShouldHandleCyrillic()
    {
        var view = HistoryUiDiff.Build(
            HistoryFixtures.Transcript((0, 5, "Звонок поставщику ещё")),
            HistoryFixtures.Transcript((0, 5, "Звонок поставщику еще")));
        Assert.True(view.IsIdentical); // ё → е is normalised away
    }
}
