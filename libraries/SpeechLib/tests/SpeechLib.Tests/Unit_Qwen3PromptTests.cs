using SpeechLib.Qwen3;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_Qwen3PromptTests
{
    [Theory]
    [InlineData("en", "English")]
    [InlineData("ru-RU", "Russian")]
    [InlineData("de", "German")]
    [InlineData("zh", "Chinese")]
    [InlineData("101", null)]
    [InlineData("auto", null)]
    public void TryNormalizeLanguage_ReturnsQwenLanguageName(string value, string? expected)
    {
        Assert.True(Qwen3Prompt.TryNormalizeLanguage(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeLanguage_RejectsUnsupportedLanguage()
    {
        Assert.False(Qwen3Prompt.TryNormalizeLanguage("xx", out _));
    }

    [Theory]
    [InlineData("language English", "English")]
    [InlineData("language Russian", "Russian")]
    [InlineData("language Chinese", "Chinese")]
    public void TryDetectLanguage_ExtractsDecoderLanguageMarker(string text, string expected)
    {
        Assert.True(Qwen3Prompt.TryDetectLanguage(text, out var detected));
        Assert.Equal(expected, detected);
    }

    [Fact]
    public void TryDetectLanguage_IgnoresUnknownPrefix()
    {
        Assert.False(Qwen3Prompt.TryDetectLanguage("language Klingon", out _));
    }

    [Fact]
    public void AutoLanguageTracker_LocksAfterTwoMatchingMarkers()
    {
        var tracker = new Qwen3AutoLanguageTracker();

        Assert.True(tracker.Observe("language Russian", out var firstLock));
        Assert.Null(firstLock);

        Assert.True(tracker.Observe("language Russian", out var secondLock));
        Assert.Equal("Russian", secondLock);
    }

    [Fact]
    public void AutoLanguageTracker_ReplacesUnstableCandidate()
    {
        var tracker = new Qwen3AutoLanguageTracker();

        Assert.True(tracker.Observe("language English", out var firstLock));
        Assert.Null(firstLock);
        Assert.True(tracker.Observe("language Russian", out var secondLock));
        Assert.Null(secondLock);
        Assert.True(tracker.Observe("language Russian", out var thirdLock));
        Assert.Equal("Russian", thirdLock);
    }

    [Fact]
    public void BuildPromptIds_InsertsSystemAndLanguagePromptAroundAudioTurn()
    {
        var system = new long[] { 1001, 1002 };
        var language = new long[] { 2001, 2002 };

        var prompt = Qwen3Prompt.BuildPromptIds(3, system, language, out var audioOffset);

        Assert.Equal(Qwen3Prompt.AudioPadTokenId, prompt[audioOffset]);
        Assert.Equal(Qwen3Prompt.AudioPadTokenId, prompt[audioOffset + 2]);
        Assert.Equal(system, prompt.Skip(3).Take(2));
        Assert.Equal(language, prompt[^2..]);
    }
}