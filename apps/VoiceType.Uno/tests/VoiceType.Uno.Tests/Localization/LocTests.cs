using System.Globalization;
using VoiceType.Uno.Localization;
using Xunit;

namespace VoiceType.Uno.Tests.Localization;

/// <summary>Switching languages changes process-wide state: these tests run alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageSwitchCollection
{
    public const string Name = "Interface language switch";
}

[Collection(LanguageSwitchCollection.Name)]
public sealed class LocTests
{
    [Theory]
    [InlineData("ru", 1, "one")]
    [InlineData("ru", 2, "few")]
    [InlineData("ru", 5, "many")]
    [InlineData("ru", 11, "many")]
    [InlineData("ru", 12, "many")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 22, "few")]
    [InlineData("ru", 0, "many")]
    [InlineData("uk", 111, "many")]
    [InlineData("pl", 1, "one")]
    [InlineData("pl", 21, "many")]
    [InlineData("pl", 23, "few")]
    [InlineData("ar", 0, "zero")]
    [InlineData("ar", 2, "two")]
    [InlineData("ar", 3, "few")]
    [InlineData("ar", 11, "many")]
    [InlineData("ar", 100, "other")]
    [InlineData("fr", 0, "one")]
    [InlineData("pt-BR", 0, "one")]
    [InlineData("en", 0, "other")]
    [InlineData("en", 1, "one")]
    [InlineData("ja", 1, "other")]
    [InlineData("zh-CN", 1, "other")]
    public void Category_ShouldFollowCldr(string code, long n, string expected) =>
        Assert.Equal(expected, PluralRules.Category(code, n));

    [Theory]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("zh-TW", "zh-CN")]
    [InlineData("de-AT", "de")]
    [InlineData("ru-RU", "ru")]
    [InlineData("en-GB", "en")]
    [InlineData("sv-SE", "en")]
    [InlineData("ar-EG", "ar")]
    public void MatchSystem_ShouldPickTheClosestLanguage(string culture, string expected) =>
        Assert.Equal(expected, Loc.MatchSystem(CultureInfo.GetCultureInfo(culture)).Code);

    [Fact]
    public void MatchSystem_Invariant_ShouldBeEnglish() =>
        Assert.Equal("en", Loc.MatchSystem(CultureInfo.InvariantCulture).Code);

    [Fact]
    public void SetLanguage_ShouldSwitchTextsAndRaiseTheEvent()
    {
        var raised = 0;
        void OnChanged() => raised++;
        Loc.LanguageChanged += OnChanged;
        try
        {
            Assert.Equal("Continue", Loc.T("Language.Prompt.Continue"));
            Assert.True(Loc.SetLanguage("ru"));
            Assert.Equal("Продолжить", Loc.T("Language.Prompt.Continue"));
            Assert.Equal("ru", Loc.Culture.Name);
            Assert.False(Loc.SetLanguage("RU"));
            Assert.Equal(1, raised);
        }
        finally
        {
            Loc.LanguageChanged -= OnChanged;
            Loc.SetLanguage("en");
        }
        Assert.Equal("Continue", Loc.T("Language.Prompt.Continue"));
    }

    [Fact]
    public void SetLanguage_Unknown_ShouldBeEnglish()
    {
        try
        {
            Loc.SetLanguage("ru");
            Loc.SetLanguage("xx-YY");
            Assert.Equal("en", Loc.Current.Code);
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void T_UnknownKey_ShouldShowTheKey() => Assert.Equal("No.Such.Key", Loc.T("No.Such.Key"));

    [Fact]
    public void In_ShouldReadAnotherLanguageWithoutSwitching()
    {
        Assert.Equal("Продолжить", Loc.In("ru", "Language.Prompt.Continue"));
        Assert.Equal("en", Loc.Current.Code);
    }
}
