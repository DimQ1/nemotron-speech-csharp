using SpeechLib.Translation;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_TranslationOutputCleanerTests
{
    [Theory]
    [InlineData("Translation: Привет, мир.", "Привет, мир.")]
    [InlineData("Перевод: Привет, мир.", "Привет, мир.")]
    [InlineData("\"Привет, мир.\"", "Привет, мир.")]
    [InlineData("«Привет, мир.»", "Привет, мир.")]
    [InlineData("```\nПривет, мир.\n```", "Привет, мир.")]
    [InlineData("{\"translation\": \"Привет, мир.\"}", "Привет, мир.")]
    [InlineData("Привет,<unk> мир.", "Привет, мир.")]
    [InlineData("  Привет,   мир.  ", "Привет, мир.")]
    public void Clean_StripsWrappersAndLabels(string raw, string expected)
    {
        Assert.Equal(expected, TranslationOutputCleaner.Clean(raw));
    }

    [Fact]
    public void Clean_KeepsQuotedFragmentsInsideText()
    {
        Assert.Equal("He said \"yes\" and left.", TranslationOutputCleaner.Clean("He said \"yes\" and left."));
    }

    [Fact]
    public void Clean_CutsRepetitionLoops()
    {
        var looped = "Он сказал да да да да да да да";
        Assert.Equal("Он сказал да", TranslationOutputCleaner.Clean(looped));

        var phraseLoop = "We are going to the store and then and then and then and then again";
        Assert.Equal("We are going to the store and then", TranslationOutputCleaner.Clean(phraseLoop));
    }

    [Fact]
    public void Clean_LeavesNormalTextAlone()
    {
        const string text = "Joe Keaton disapproved of films, and Buster also had reservations about the medium.";
        Assert.Equal(text, TranslationOutputCleaner.Clean(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clean_EmptyInputYieldsEmpty(string? raw)
    {
        Assert.Equal("", TranslationOutputCleaner.Clean(raw));
    }
}
