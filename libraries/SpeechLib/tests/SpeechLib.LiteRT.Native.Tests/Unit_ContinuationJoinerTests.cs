namespace SpeechLib.LiteRT.Native.Tests;

public sealed class Unit_ContinuationJoinerTests
{
    private static string Join(string partial, params string[] chunks)
    {
        var joiner = new ContinuationJoiner(partial);
        var text = new System.Text.StringBuilder();
        foreach (var chunk in chunks)
            text.Append(joiner.Push(chunk));
        text.Append(joiner.Flush());
        return text.ToString();
    }

    [Fact]
    public void Push_ShouldDropRestartedSentenceOverlap()
    {
        var partial = "It has four parts.\n\nThe cycle begins in ";

        var result = Join(partial, "The cycle ", "begins in the ", "**evaporator**, where it boils.");

        Assert.Equal("the **evaporator**, where it boils.", result);
    }

    [Fact]
    public void Push_ShouldDropOverlapEvenWhenModelOmitsTheTrailingSpace()
    {
        var result = Join("The cycle begins in ", "The cycle begins in the evaporator and more text follows here.");

        Assert.Equal("the evaporator and more text follows here.", result);
    }

    [Fact]
    public void Push_ShouldDropLeadingEllipsisAndKeepWordsApart()
    {
        var result = Join("from the establishment of the Roman", "...to the collapse of the Western Roman Empire in 476 AD.");

        Assert.Equal(" to the collapse of the Western Roman Empire in 476 AD.", result);
    }

    [Fact]
    public void Push_ShouldPassThroughAFreshContinuationUnchanged()
    {
        var result = Join("The compressor raises the pressure and ", "temperature of the refrigerant, which then flows to the condenser.");

        Assert.Equal("temperature of the refrigerant, which then flows to the condenser.", result);
    }

    [Fact]
    public void Push_ShouldNotTreatShortCoincidencesAsOverlap()
    {
        var result = Join("and the ", "the cycle continues with a long enough text to pass the look-ahead.");

        Assert.StartsWith("the cycle continues", result);
    }

    [Fact]
    public void Flush_ShouldReleaseShortContinuationHeldForLookahead()
    {
        var result = Join("Some partial answer text that is long enough ", "ok.");

        Assert.Equal("ok.", result);
    }

    [Fact]
    public void Push_ShouldStreamImmediatelyOnceDecided()
    {
        var joiner = new ContinuationJoiner("abc ");

        Assert.Equal("", joiner.Push("de"));
        Assert.Equal("def ghij", joiner.Push("f ghij")); // look-ahead (len 4 + 4) reached
        Assert.Equal("next", joiner.Push("next"));
    }
}
