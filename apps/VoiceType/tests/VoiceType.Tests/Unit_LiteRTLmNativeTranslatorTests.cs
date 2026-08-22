using SpeechLib.LiteRT.Native;
using Xunit;

namespace VoiceType.Tests;

public sealed class Unit_LiteRTLmNativeTranslatorTests
{
    [Fact]
    public void Constructor_WithNullOptions_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new LiteRTLmNativeTranslator(null!));
    }

    [Fact]
    public void Constructor_WithBlankModelPath_ShouldThrow()
    {
        var options = new LiteRTLmNativeOptions { ModelPath = "  " };

        Assert.Throws<ArgumentException>(() => new LiteRTLmNativeTranslator(options));
    }

    [Fact]
    public void Constructor_WithNonexistentModelFile_ShouldThrow()
    {
        var options = new LiteRTLmNativeOptions
        {
            ModelPath = Path.Combine(Path.GetTempPath(), "no-such-model.litertlm"),
        };

        Assert.Throws<FileNotFoundException>(() => new LiteRTLmNativeTranslator(options));
    }

    [Fact]
    public void BuildSystemPrompt_ShouldMentionTargetLanguage()
    {
        var options = new LiteRTLmNativeOptions { ModelPath = "unused.litertlm" };

        var prompt = options.BuildSystemPrompt("ru", null);

        Assert.Contains("ru", prompt);
        Assert.Contains("translation", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSystemPrompt_WithSourceLanguage_ShouldMentionSource()
    {
        var options = new LiteRTLmNativeOptions { ModelPath = "unused.litertlm" };

        var prompt = options.BuildSystemPrompt("de", "en");

        Assert.Contains("en", prompt);
        Assert.Contains("de", prompt);
    }
}
