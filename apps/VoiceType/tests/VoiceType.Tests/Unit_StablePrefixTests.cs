using SpeechLib.Translation;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// Unit tests for <see cref="StablePrefix.LongestWordAlignedCommonPrefix"/>, which
/// locks the stable prefix of two successive provisional translations.
/// </summary>
public class Unit_StablePrefixTests
{
    [Theory]
    [InlineData("Привет мир", "Привет мир как", "Привет мир")]
    [InlineData("Привет мир", "Привет мир.", "Привет мир")]
    [InlineData("Я иду домой", "Я иду домой сейчас", "Я иду домой")]
    [InlineData("Hello world", "Hello world and everyone", "Hello world")]
    [InlineData("Привет мир", "Привет мир", "Привет мир")]
    public void LongestWordAlignedCommonPrefix_ShouldLockSharedWords(string previous, string current, string expected)
    {
        var result = StablePrefix.LongestWordAlignedCommonPrefix(previous, current);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("я иду", "я шёл")]
    [InlineData("кот", "котэ")]
    [InlineData("один", "одна")]
    public void LongestWordAlignedCommonPrefix_ShouldReturnEmpty_WhenLessThanTwoStableWords(string previous, string current)
    {
        var result = StablePrefix.LongestWordAlignedCommonPrefix(previous, current);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void LongestWordAlignedCommonPrefix_ShouldHonorMinWordsThreshold()
    {
        var result = StablePrefix.LongestWordAlignedCommonPrefix("Привет мир", "Привет мир как", minWords: 3);

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("", "что-то")]
    [InlineData("что-то", "")]
    [InlineData(null, "что-то")]
    [InlineData("что-то", null)]
    public void LongestWordAlignedCommonPrefix_ShouldReturnEmpty_ForEmptyOrNullInput(string? previous, string? current)
    {
        var result = StablePrefix.LongestWordAlignedCommonPrefix(previous!, current!);

        Assert.Equal(string.Empty, result);
    }
}
