using System.Runtime.CompilerServices;
using SpeechLib.Translation;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_TranslationGuardsTests
{
    [Theory]
    [InlineData("Я не знаю, где наша палатка.", "Russian", true)]
    [InlineData("Я знаю, пошли.", "Russian", true)]
    [InlineData("Я не знаю, де їхній намет.", "Russian", false)]   // Ukrainian letters
    [InlineData("Я не знаю, де їхній намет.", "Ukrainian", true)]
    [InlineData("I don't know where our tent is.", "Russian", false)]
    [InlineData("I don't know where our tent is.", "English", false)] // Latin script is never assumed
    [InlineData("我不知道我们的帐篷在哪里。", "Chinese", true)]
    [InlineData("テントはどこですか。", "Japanese", true)]
    [InlineData("텐트가 어디 있는지 몰라요.", "Korean", true)]
    [InlineData(".", "Russian", false)]
    public void IsAlreadyInTarget_JudgesByScript(string text, string target, bool expected)
    {
        Assert.Equal(expected, TranslationGuards.IsAlreadyInTarget(text, target));
    }

    private static readonly TranslationRequest WithContext = new("Let's go.", "Russian")
    {
        PreviousSource = "I know where it is.",
        PreviousTranslation = "Я знаю, где это.",
    };

    [Fact]
    public void StripContextEcho_RemovesEchoedPreviousTranslation()
    {
        Assert.Equal("Пошли.", TranslationGuards.StripContextEcho("Я знаю, где это. Пошли.", WithContext, final: true));
        Assert.Equal("Пошли.", TranslationGuards.StripContextEcho("я знаю где это Пошли.", WithContext, final: true));
    }

    [Fact]
    public void StripContextEcho_RemovesEchoedPreviousSource()
    {
        Assert.Equal("Пошли.", TranslationGuards.StripContextEcho("I know where it is. Пошли.", WithContext, final: true));
    }

    [Fact]
    public void StripContextEcho_HidesPartialEchoWhileStreaming()
    {
        Assert.Equal("", TranslationGuards.StripContextEcho("Я знаю,", WithContext, final: false));
    }

    [Fact]
    public void StripContextEcho_KeepsGenuineRepeatAndUnrelatedText()
    {
        Assert.Equal("Я знаю, где это.", TranslationGuards.StripContextEcho("Я знаю, где это.", WithContext, final: true));
        Assert.Equal("Пошли.", TranslationGuards.StripContextEcho("Пошли.", WithContext, final: true));
    }

    [Fact]
    public void IsRunaway_FlagsOutputFarLongerThanSource()
    {
        Assert.True(TranslationGuards.IsRunaway(new string('а', 200), "Пошли."));
        Assert.False(TranslationGuards.IsRunaway("Пошли.", "Let's go."));
    }

    /// <summary>A model that echoes the context turn before its answer — the failure seen with Gemma.</summary>
    private sealed class EchoingTranslator : ITextTranslator
    {
        public int Calls;

        public Task<string?> TranslateAsync(string text, string targetLang, string? sourceLang = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("[" + text + "]");

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            TranslationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield();
            if (request.HasContext)
                yield return request.PreviousTranslation + " ";
            yield return "[" + request.Text + "]";
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Session_DoesNotAccumulateEchoedContext()
    {
        var translator = new EchoingTranslator();
        await using var session = new LiveTranslationSession(
            _ => Task.FromResult<ITextTranslator>(translator),
            new LiveTranslationOptions { DraftDebounceMs = 5 }) { TargetLanguage = "German" };

        session.Feed("One. Two. Three. Four.");
        await session.FlushAsync();

        Assert.Equal(
            string.Join(Environment.NewLine, "[One.]", "[Two.]", "[Three.]", "[Four.]"),
            session.DisplayText);
    }

    [Fact]
    public async Task Session_PassesThroughTextAlreadyInTargetLanguage()
    {
        var translator = new EchoingTranslator();
        await using var session = new LiveTranslationSession(
            _ => Task.FromResult<ITextTranslator>(translator),
            new LiveTranslationOptions { DraftDebounceMs = 5 }) { TargetLanguage = "Russian" };

        session.Feed("Давай в палатку. Я знаю, пошли.");
        await session.FlushAsync();

        Assert.Equal("Давай в палатку." + Environment.NewLine + "Я знаю, пошли.", session.DisplayText);
        Assert.Equal(0, translator.Calls);
    }
}
