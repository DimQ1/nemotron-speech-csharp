using SpeechLib.Agents;
using Xunit;

namespace SpeechLib.Agents.Tests;

public class CitationTests
{
    private static List<ChatSourceDocument> Two() =>
    [
        Transcripts.Source("S1", "r1", Transcripts.English()),     // 0..90 s, a line every 9 s
        Transcripts.Source("S2", "r2", Transcripts.Russian()),     // 0..56 s
    ];

    [Fact]
    public void Parse_ShouldResolveAliasAndSnapToTheSegmentStart()
    {
        var parsed = CitationParser.Parse("Maria sends the forecast [S1 00:20].", Two());

        var c = Assert.Single(parsed.Citations);
        Assert.Equal("r1", c.RecordingId);
        Assert.Equal(18, c.StartSec);
        Assert.Contains("Maria", c.Quote);
        Assert.Equal("Maria sends the forecast [S1 00:18].", parsed.Text);
    }

    [Fact]
    public void Parse_ShouldMoveACitationToTheNearbyLineThatSupportsTheClaim()
    {
        // The model cited 00:10 (the cloud-costs line) for a claim that is really the line at 00:18.
        var parsed = CitationParser.Parse("- Maria will send the updated forecast to finance [S1 00:10]\n- Cloud costs are over budget [S1 00:10]", Two());

        Assert.Equal([18.0, 9.0], parsed.Citations.Select(c => c.StartSec));
        Assert.Contains("Maria will send", parsed.Citations[0].Quote);
        Assert.Contains("[S1 00:18]", parsed.Text);
    }

    [Fact]
    public void Parse_ShouldNotCiteAnAnswerThatSaysTheTranscriptHasNothing()
    {
        var parsed = CitationParser.Parse("There is no information about the Dolphin project in the transcript [S1 00:00].", Two());

        Assert.Empty(parsed.Citations);
        Assert.Equal("There is no information about the Dolphin project in the transcript.", parsed.Text);
    }

    [Fact]
    public void Parse_ShouldKeepTheSnappedLineWhenNothingNearbyMatchesTheClaim()
    {
        var parsed = CitationParser.Parse("Something unrelated entirely [S1 00:20].", Two());

        Assert.Equal(18, Assert.Single(parsed.Citations).StartSec);
    }

    [Fact]
    public void Parse_ShouldDropCitationsThatDoNotExist()
    {
        var parsed = CitationParser.Parse("A [S3 00:10] b [S1 59:59] c [S2 00:10] d.", Two());

        var c = Assert.Single(parsed.Citations);
        Assert.Equal("S2", c.Alias);
        Assert.DoesNotContain("S3", parsed.Text);
        Assert.DoesNotContain("59:59", parsed.Text);
        Assert.Equal("A b c [S2 00:08] d.", parsed.Text);
    }

    [Fact]
    public void Parse_ShouldNotCiteARemovedSource()
    {
        var sources = Two();
        sources[1] = Transcripts.Source("S2", "r2", Transcripts.Russian(), active: false);

        var parsed = CitationParser.Parse("Old claim [S2 00:10].", sources);

        Assert.Empty(parsed.Citations);
        Assert.Equal("Old claim.", parsed.Text);
    }

    [Fact]
    public void Parse_ShouldAcceptListsRangesAndBareTimesForASingleSource()
    {
        var one = new List<ChatSourceDocument> { Transcripts.Source("S1", "r1", Transcripts.English()) };

        var list = CitationParser.Parse("x [S1 00:09, 00:27] y", one);
        Assert.Equal([9.0, 27.0], list.Citations.Select(c => c.StartSec));

        var range = CitationParser.Parse("x [S1 00:09-00:30]", one);
        Assert.Equal(9, range.Citations[0].StartSec);
        Assert.True(range.Citations[0].EndSec >= 27);

        var bare = CitationParser.Parse("x [00:18] and (01:00)", one);
        Assert.Equal(2, bare.Citations.Count);
        Assert.Equal("r1", bare.Citations[0].RecordingId);
    }

    [Fact]
    public void Parse_ShouldNotGuessTheSourceOfABareTimeWhenSeveralAreActive()
    {
        var parsed = CitationParser.Parse("x [00:18] y", Two());

        Assert.Empty(parsed.Citations);
        Assert.Equal("x y", parsed.Text);
    }

    [Fact]
    public void Parse_ShouldLeaveOrdinaryBracketsAlone()
    {
        var parsed = CitationParser.Parse("See [the docs] and (the 10:30 meeting) [1].", Two());

        Assert.Empty(parsed.Citations);
        Assert.Equal("See [the docs] and (the 10:30 meeting) [1].", parsed.Text);
    }

    [Fact]
    public void Parse_ShouldDeduplicateTheSameSegment()
    {
        var parsed = CitationParser.Parse("a [S1 00:18] b [S1 00:20]", Two());

        Assert.Single(parsed.Citations);
    }

    [Fact]
    public void TimeText_ShouldRoundTripMinutesAndHours()
    {
        Assert.Equal("03:20", TimeText.Format(200));
        Assert.Equal("1:02:03", TimeText.Format(3723));
        Assert.True(TimeText.TryParse("1:02:03", out var s));
        Assert.Equal(3723, s);
        Assert.False(TimeText.TryParse("1:75", out _));
    }
}
