using SpeechLib.TextOutput;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_IncrementalTextTyperTests
{
    private static string TypeAll(IncrementalTextTyper typer, params string[] feeds) =>
        string.Concat(feeds.Select(typer.Next));

    [Fact]
    public void GrowingText_IsTypedOnceWithOriginalSpacing()
    {
        var typer = new IncrementalTextTyper();
        var typed = TypeAll(typer,
            "Раз,",
            "Раз, два, три,",
            "Раз, два, три, четыре, пять.",
            "Раз, два, три, четыре, пять. Небольшой тест",
            "Раз, два, три, четыре, пять. Небольшой тест про вставку текста.");

        Assert.Equal("Раз, два, три, четыре, пять. Небольшой тест про вставку текста.", typed);
    }

    [Fact]
    public void WordCompletedAcrossSteps_IsJoinedWithoutSpace()
    {
        var typer = new IncrementalTextTyper();
        Assert.Equal("Joe Keaton disappro", typer.Next("Joe Keaton disappro"));
        Assert.Equal("ved of films.", typer.Next("Joe Keaton disapproved of films."));
    }

    [Fact]
    public void RewrittenEarlierText_DoesNotRepeatTypedWords()
    {
        var typer = new IncrementalTextTyper();
        typer.Next("Раз, два, три.");
        // Final normalization changed spacing/casing of earlier words and added more.
        Assert.Equal(" четыре, пять.", typer.Next("раз,  два, три. четыре, пять."));
    }

    [Fact]
    public void FirstTextOfSession_DropsLeadingPunctuation()
    {
        var typer = new IncrementalTextTyper();
        Assert.Equal("Hello.", typer.Next(". Hello."));
    }

    [Fact]
    public void NextSession_ContinuesWithSingleSpace()
    {
        var typer = new IncrementalTextTyper();
        typer.Next("First session.");
        typer.Reset();
        Assert.Equal(" Second one.", typer.Next("Second one."));
    }

    [Fact]
    public void SkipTo_MarksTextAsPresent()
    {
        var typer = new IncrementalTextTyper();
        typer.SkipTo("Already there.");
        Assert.Equal(" New words.", typer.Next("Already there. New words."));
    }

    [Fact]
    public void SameOrEmptyText_TypesNothing()
    {
        var typer = new IncrementalTextTyper();
        typer.Next("Hello.");
        Assert.Equal("", typer.Next("Hello."));
        Assert.Equal("", typer.Next(""));
    }
}
