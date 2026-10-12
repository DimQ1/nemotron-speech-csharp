using VoiceType.Uno.History;
using VoiceType.Uno.Services.Playback;
using Xunit;

namespace VoiceType.Uno.Tests.Playback;

public class PlaybackCursorTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static TranscriptSegment Segment(int i, double start, double end, string text, params (string W, double S, double E)[] words) =>
        new()
        {
            Index = i,
            Start = start,
            End = end,
            Text = text,
            Words = words.Select(w => new TranscriptWord(w.W, w.S, w.E)).ToList(),
        };

    // 0: [1.0, 3.0) "Okay, let's start."  1: [4.0, 6.0) ""  2: [7.0, 9.5) "Next point."
    private static readonly List<TranscriptSegment> Segments =
    [
        Segment(0, 1.0, 3.0, "Okay, let's start.", ("Okay,", 1.0, 1.4), ("let's", 1.5, 2.0), ("start.", 2.1, 2.9)),
        Segment(1, 4.0, 6.0, ""),
        Segment(2, 7.0, 9.5, "Next point.", ("Next", 7.0, 7.5), ("point.", 7.6, 9.4)),
    ];

    [Theory]
    [InlineData(0.0, -1, -1)]   // before the first segment
    [InlineData(0.999, -1, -1)]
    [InlineData(1.0, 0, 0)]     // exactly at the start
    [InlineData(1.45, 0, 0)]    // gap between words keeps the previous word
    [InlineData(1.5, 0, 1)]
    [InlineData(2.95, 0, 2)]
    [InlineData(3.5, 0, -1)]    // pause after the segment: segment stays, no word
    [InlineData(5.0, 0, -1)]    // empty segment is skipped in the highlight
    [InlineData(7.0, 2, 0)]
    [InlineData(9.0, 2, 1)]
    [InlineData(60.0, 2, -1)]   // after the end
    public void Locate_ShouldFindSegmentAndWord(double seconds, int segment, int word)
    {
        var location = PlaybackCursor.Locate(Segments, S(seconds));

        Assert.Equal(new PlaybackLocation(segment, word), location);
    }

    [Fact]
    public void Locate_ShouldHandleEmptyTranscriptAndSegmentsWithoutWords()
    {
        Assert.Equal(PlaybackLocation.None, PlaybackCursor.Locate([], S(3)));
        var noWords = new List<TranscriptSegment> { Segment(0, 0, 5, "text without timings") };
        Assert.Equal(new PlaybackLocation(0, -1), PlaybackCursor.Locate(noWords, S(2)));
    }

    [Fact]
    public void Locate_ShouldScaleToLongTranscripts()
    {
        var many = Enumerable.Range(0, 20_000).Select(i => Segment(i, i * 3.0, i * 3.0 + 2.5, $"s{i}")).ToList();

        Assert.Equal(12_345, PlaybackCursor.Locate(many, S(12_345 * 3.0 + 1)).SegmentIndex);
    }

    [Theory]
    [InlineData(9.2, 7.0)]   // > 2 s into "Next point." → its own start
    [InlineData(7.5, 1.0)]   // near its start → previous non-empty segment (skips the empty one)
    [InlineData(2.0, 0.0)]   // near the first segment start → 0
    [InlineData(0.5, 0.0)]   // before any segment
    public void PreviousSegmentStart_ShouldFollowMediaPlayerConvention(double position, double expected)
    {
        Assert.Equal(S(expected), PlaybackCursor.PreviousSegmentStart(Segments, S(position)));
    }

    [Fact]
    public void NextSegmentStart_ShouldSkipEmptySegmentsAndEndWithNull()
    {
        Assert.Equal(S(1.0), PlaybackCursor.NextSegmentStart(Segments, S(0)));
        Assert.Equal(S(7.0), PlaybackCursor.NextSegmentStart(Segments, S(1.0)));
        Assert.Null(PlaybackCursor.NextSegmentStart(Segments, S(7.0)));
    }

    [Theory]
    [InlineData(5, -10, 0)]
    [InlineData(25, -10, 15)]
    [InlineData(25, 10, 30)]
    [InlineData(28, 10, 30)]
    public void Skip_ShouldClampToTheRecording(double position, double delta, double expected)
    {
        Assert.Equal(S(expected), PlaybackCursor.Skip(S(position), S(delta), S(30)));
    }

    [Fact]
    public void NextSpeed_ShouldCycleThroughTheList()
    {
        var seen = new List<double>();
        double speed = 1.0;
        for (int i = 0; i < 5; i++)
        {
            speed = PlaybackCursor.NextSpeed(speed);
            seen.Add(speed);
        }

        Assert.Equal([1.25, 1.5, 2.0, 0.75, 1.0], seen);
        Assert.Equal(1.0, PlaybackCursor.NextSpeed(0.8)); // unlisted → next larger
        Assert.All(PlaybackCursor.Speeds, s => Assert.InRange(s, PlaybackCursor.MinSpeed, PlaybackCursor.MaxSpeed));
    }

    [Theory]
    [InlineData(1.0, "1×")]
    [InlineData(1.25, "1.25×")]
    [InlineData(0.75, "0.75×")]
    [InlineData(2.0, "2×")]
    public void FormatSpeed_ShouldDropTrailingZeros(double speed, string expected)
    {
        Assert.Equal(expected, PlaybackCursor.FormatSpeed(speed));
    }

    [Theory]
    [InlineData(204.9, 754, "3:24")]
    [InlineData(0, 754, "0:00")]
    [InlineData(65, 3700, "0:01:05")]
    [InlineData(3723, 3800, "1:02:03")]
    [InlineData(-3, 60, "0:00")]
    public void FormatTime_ShouldMatchTheTotalShape(double seconds, double total, string expected)
    {
        Assert.Equal(expected, PlaybackCursor.FormatTime(S(seconds), S(total)));
    }

    [Fact]
    public void SegmentTicks_ShouldBeFractionsOfTheDuration()
    {
        Assert.Equal([0.1, 0.7], PlaybackCursor.SegmentTicks(Segments, S(10)));
        Assert.Empty(PlaybackCursor.SegmentTicks(Segments, TimeSpan.Zero));
    }
}
