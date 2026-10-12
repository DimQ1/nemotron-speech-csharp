using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class TranscriptDiffTests
{
    private static TranscriptDocument T(params string[] segments) =>
        HistoryFixtures.Transcript(segments.Select((s, i) => ((double)i * 5, (double)i * 5 + 4, s)).ToArray());

    [Fact]
    public void Compute_ShouldReportNoChangesForIdenticalVersions()
    {
        var result = TranscriptDiff.Compute(T("one two three", "four five"), T("one two three", "four five"));

        Assert.True(result.IsIdentical);
        Assert.Equal(0, result.ChangedPercent);
        Assert.Empty(result.Segments);
        Assert.Equal(5, result.ReferenceWords);
    }

    [Fact]
    public void Compute_ShouldIgnoreCasePunctuationAndSegmentation()
    {
        var result = TranscriptDiff.Compute(T("Okay, let's start.", "The meeting moves."), T("okay lets start the", "meeting — moves"));

        Assert.True(result.IsIdentical);
    }

    [Fact]
    public void Compute_ShouldCountSubstitutionsDeletionsAndInsertions()
    {
        var a = T("the meeting moves to nine next monday");
        var b = T("the meeting moves to ten monday please");

        var result = TranscriptDiff.Compute(a, b);

        Assert.Equal(1, result.Substitutions);
        Assert.Equal(1, result.Deletions);
        Assert.Equal(1, result.Insertions);
        Assert.Equal(3, result.WordsChanged);
        Assert.Equal(100.0 * 3 / 7, result.ChangedPercent, 6);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(0, segment.SegmentIndex);
        Assert.Contains(segment.Changes, c => c.Kind == DiffKind.Replace && c.OldText == "nine next" && c.NewText == "ten");
        Assert.Contains(segment.Changes, c => c.Kind == DiffKind.Insert && c.NewText == "please");
    }

    [Fact]
    public void Compute_ShouldAnchorChangesToSegmentsOfB()
    {
        var a = T("alpha beta", "gamma delta", "epsilon zeta");
        var b = T("alpha beta", "gamma DELTA!", "epsilon eta");

        var result = TranscriptDiff.Compute(a, b);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(2, segment.SegmentIndex);
        Assert.Equal(1, result.SegmentsChanged);
        Assert.Equal(1, segment.Substitutions);
    }

    [Fact]
    public void Compute_ShouldHandleCyrillicAndYo()
    {
        var result = TranscriptDiff.Compute(T("Ещё одна ёлка"), T("еще одна елка"));

        Assert.True(result.IsIdentical);
    }

    [Fact]
    public void Compute_ShouldHandleEmptySides()
    {
        var fromEmpty = TranscriptDiff.Compute(T(), T("new words"));
        var toEmpty = TranscriptDiff.Compute(T("old words"), T());

        Assert.Equal(2, fromEmpty.Insertions);
        Assert.Equal(100, fromEmpty.ChangedPercent);
        Assert.Equal(2, toEmpty.Deletions);
        Assert.Equal(-1, toEmpty.Segments.Single().SegmentIndex);
    }

    [Fact]
    public void Align_ShouldProduceMinimalScript()
    {
        var hunks = WordAligner.Align(["a", "b", "c", "a", "b", "b", "a"], ["c", "b", "a", "b", "a", "c"]);

        var edits = hunks.Where(h => h.Kind != DiffKind.Equal).Sum(h => h.ACount + h.BCount);
        Assert.Equal(5, edits); // Myers' paper example: D = 5
        Assert.Equal(7, hunks.Sum(h => h.ACount));
        Assert.Equal(6, hunks.Sum(h => h.BCount));
    }

    [Fact]
    public void Align_ShouldFallBackToReplaceAboveMaxEditDistance()
    {
        var a = Enumerable.Range(0, 50).Select(i => "a" + i).ToList();
        var b = Enumerable.Range(0, 50).Select(i => "b" + i).ToList();

        var hunks = WordAligner.Align(a, b, maxEditDistance: 10);

        var hunk = Assert.Single(hunks);
        Assert.Equal(DiffKind.Replace, hunk.Kind);
    }

    [Fact]
    public void Align_ShouldHandleLongSimilarSequences()
    {
        var a = Enumerable.Range(0, 20000).Select(i => "w" + i % 997).ToList();
        var b = a.ToList();
        b[5000] = "changed";
        b.Insert(15000, "inserted");

        var hunks = WordAligner.Align(a, b);

        Assert.Equal(3, hunks.Where(h => h.Kind != DiffKind.Equal).Sum(h => h.ACount + h.BCount));
    }

    // ── Timing carry-over ───────────────────────────────────────────────────

    private static TranscriptDocument Timed() => new()
    {
        Segments =
        [
            new TranscriptSegment
            {
                Index = 0, Start = 0, End = 4, Text = "meet at nine today", WordTiming = WordTimingKinds.Decoder,
                Words = [new("meet", 0, 0.5), new("at", 0.6, 0.8), new("nine", 1.0, 1.6), new("today", 2.0, 3.0)],
            },
            new TranscriptSegment
            {
                Index = 1, Start = 5, End = 7, Text = "thanks all", WordTiming = WordTimingKinds.Decoder,
                Words = [new("thanks", 5.0, 5.8), new("all", 6.0, 6.5)],
            },
        ],
    };

    [Fact]
    public void CarryOver_ShouldKeepUnchangedTimingsAndInterpolateReplacedWords()
    {
        var edited = TranscriptTimings.CarryOver(Timed(), ["meet at ten tomorrow", "thanks all"], "v2");

        var words = edited.Segments[0].Words;
        Assert.Equal(["meet", "at", "ten", "tomorrow"], words.Select(w => w.Text));
        Assert.Equal((0.0, 0.5), (words[0].Start, words[0].End));
        Assert.Equal((0.6, 0.8), (words[1].Start, words[1].End));
        // "ten tomorrow" (3 + 8 chars) takes over the span of "nine today": 1.0 .. 3.0
        Assert.Equal(1.0, words[2].Start);
        Assert.Equal(1.545, words[2].End);
        Assert.Equal(1.545, words[3].Start);
        Assert.Equal(3.0, words[3].End);
        Assert.Equal(WordTimingKinds.Estimated, edited.Segments[0].WordTiming);
        Assert.Equal(WordTimingKinds.Decoder, edited.Segments[1].WordTiming);
        Assert.Equal("v2", edited.Version);
        Assert.Equal("meet at ten tomorrow thanks all", edited.Text);
    }

    [Fact]
    public void CarryOver_ShouldPlaceInsertedWordsInTheGap()
    {
        var edited = TranscriptTimings.CarryOver(Timed(), ["meet at nine sharp today", "thanks all"], "v2");

        var sharp = edited.Segments[0].Words[3];
        Assert.Equal("sharp", sharp.Text);
        Assert.Equal(1.6, sharp.Start);
        Assert.Equal(2.0, sharp.End);
        Assert.Equal(2.0, edited.Segments[0].Words[4].Start);
    }

    [Fact]
    public void CarryOver_ShouldKeepTimingsWhenOnlyCaseOrPunctuationChanged()
    {
        var edited = TranscriptTimings.CarryOver(Timed(), ["Meet at nine, today.", "thanks all"], "v2");

        Assert.Equal(1.0, edited.Segments[0].Words[2].Start);
        Assert.Equal("nine,", edited.Segments[0].Words[2].Text);
    }

    [Fact]
    public void CarryOver_ShouldDropSegmentsEditedToEmpty()
    {
        var edited = TranscriptTimings.CarryOver(Timed(), ["", "thanks all"], "v2");

        var segment = Assert.Single(edited.Segments);
        Assert.Equal(0, segment.Index);
        Assert.Equal(5, segment.Start);
    }

    [Fact]
    public void CarryOver_ShouldRejectWrongSegmentCount() =>
        Assert.Throws<ArgumentException>(() => TranscriptTimings.CarryOver(Timed(), ["only one"], "v2"));

    [Fact]
    public void CarryOverFullText_ShouldMapWordsBackToTheirSegments()
    {
        var edited = TranscriptTimings.CarryOver(Timed(), "meet at ten today thanks everyone all", "v2");

        Assert.Equal(["meet at ten today", "thanks everyone all"], edited.Segments.Select(s => s.Text));
        Assert.Equal(1.0, edited.Segments[0].Words[2].Start);
        Assert.Equal(1.6, edited.Segments[0].Words[2].End);
        Assert.Equal(5.8, edited.Segments[1].Words[1].Start);
        Assert.Equal(6.0, edited.Segments[1].Words[1].End);
        Assert.Equal(6.0, edited.Segments[1].Words[2].Start);
    }

    [Fact]
    public void CarryOver_ShouldEstimateWhenSourceHasNoWordTimings()
    {
        var source = new TranscriptDocument
        {
            Segments = [new TranscriptSegment { Start = 0, End = 4, Text = "aa bb" }],
        };

        var edited = TranscriptTimings.CarryOver(source, ["aa bb"], "v2");

        Assert.Equal([(0.0, 2.0), (2.0, 4.0)], edited.Segments[0].Words.Select(w => (w.Start, w.End)));
    }
}
