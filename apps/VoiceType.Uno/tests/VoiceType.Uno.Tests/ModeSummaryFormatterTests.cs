using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class ModeSummaryFormatterTests
{
    [Fact]
    public void Format_ShouldListModelAndLanguageWhenNoModeIsOn()
    {
        var summary = ModeSummaryFormatter.Format("Parakeet TDT 0.6B v3", "auto", null, false);

        Assert.Equal("Parakeet TDT 0.6B v3 · Auto", summary);
    }

    [Fact]
    public void Format_ShouldAppendTranslationTargetAndInjection()
    {
        var summary = ModeSummaryFormatter.Format("Parakeet TDT 0.6B v3", "en", "ru", true);

        Assert.Equal("Parakeet TDT 0.6B v3 · EN · → RU · Inject", summary);
    }

    [Fact]
    public void Format_ShouldSkipABlankTranslationTarget()
    {
        var summary = ModeSummaryFormatter.Format("Model", "de", " ", true);

        Assert.Equal("Model · DE · Inject", summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_ShouldFallBackWhenTheModelNameIsBlank(string? model)
    {
        var summary = ModeSummaryFormatter.Format(model, "auto", null, false);

        Assert.Equal("No model · Auto", summary);
    }

    [Theory]
    [InlineData(null, "Auto")]
    [InlineData("", "Auto")]
    [InlineData("AUTO", "Auto")]
    [InlineData("ru", "RU")]
    [InlineData(" en ", "EN")]
    public void FormatLanguage_ShouldShowAutoOrAnUpperCaseCode(string? code, string expected)
    {
        Assert.Equal(expected, ModeSummaryFormatter.FormatLanguage(code));
    }
}
