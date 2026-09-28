using SpeechLib.Translation;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_TranslationLanguagesTests
{
    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("EN", "English")]
    [InlineData("en-US", "English")]
    [InlineData("German", "German")]
    [InlineData("Belarusian", "Belarusian")]
    public void NameFor_ResolvesCodesAndKeepsNames(string input, string expected)
    {
        Assert.Equal(expected, TranslationLanguages.NameFor(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void NameFor_RejectsAutoAndBlank(string? input)
    {
        Assert.Null(TranslationLanguages.NameFor(input));
        Assert.False(TranslationLanguages.IsTargetCode(input));
    }

    [Fact]
    public void All_HasUniqueCodes()
    {
        Assert.Equal(TranslationLanguages.All.Count, TranslationLanguages.All.Select(l => l.Code).Distinct().Count());
    }
}
