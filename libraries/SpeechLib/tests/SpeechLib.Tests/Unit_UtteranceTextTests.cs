using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_UtteranceTextTests
{
    [Theory]
    [InlineData(". You are so rude", true)]
    [InlineData("? Yes", true)]
    [InlineData(", and then", true)]
    [InlineData("... and then", true)]
    [InlineData(". <en-US> You are so rude", true)]
    [InlineData("<en-US> . You are", true)]
    [InlineData("  .", true)]
    [InlineData("你好。 是的", false)]
    [InlineData("。 是的", true)]
    [InlineData("You are so rude", false)]
    [InlineData(".5 kg", false)]
    [InlineData("...and", false)]
    [InlineData("<en-US> You are", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StartsWithMark_ShouldRecognizeAMarkThatIsATokenOfItsOwn(string? text, bool expected) =>
        Assert.Equal(expected, UtteranceText.StartsWithMark(text));

    [Theory]
    [InlineData("They went home", "You are so rude", "They went home You are so rude")]
    [InlineData("They went home", ". You are so rude", "They went home. You are so rude")]
    [InlineData("They went home ", ". You are so rude", "They went home. You are so rude")]
    [InlineData("They went home", ".", "They went home.")]
    [InlineData("They went home", ". <en-US> You are", "They went home. <en-US> You are")]
    [InlineData("Wait", "? Yes", "Wait? Yes")]
    [InlineData("", "Hello", "Hello")]
    [InlineData(null, "Hello", "Hello")]
    [InlineData("Hello", "", "Hello")]
    [InlineData("Hello", "   ", "Hello")]
    [InlineData("Hello", null, "Hello")]
    [InlineData(null, null, "")]
    public void Join_ShouldAttachALeadingMarkWithoutASpace(string? previous, string? next, string expected) =>
        Assert.Equal(expected, UtteranceText.Join(previous, next));

    [Theory]
    [InlineData("able", "s are", "ables are")]
    [InlineData("able ", "s are", "ables are")]
    [InlineData("there", ". You", "there. You")]
    [InlineData("", "s are", "s are")]
    public void Join_WithTheContinuationFlag_ShouldLeaveOutTheSpaceEvenWithoutAMark(string previous, string next, string expected) =>
        Assert.Equal(expected, UtteranceText.Join(previous, next, continuation: true));

    [Theory]
    [InlineData("They went home", "You are so rude", false, "They went home You are so rude")]
    [InlineData("They went home", ". You are so rude", false, "They went home. You are so rude")]
    [InlineData("They went home ", ". You are so rude", false, "They went home. You are so rude")]
    [InlineData("able", "s are", true, "ables are")]
    [InlineData("", "Hello", false, "Hello")]
    [InlineData("   ", "Hello", false, "Hello")]
    [InlineData("Hello", "", false, "Hello")]
    [InlineData("Hello ", null, false, "Hello ")]
    public void Append_ShouldFollowTheJoinRule(string previous, string? next, bool continuation, string expected)
    {
        var transcript = new System.Text.StringBuilder(previous);

        UtteranceText.Append(transcript, next, continuation);

        Assert.Equal(expected, transcript.ToString());
        Assert.Equal(expected, UtteranceText.Join(previous, next, continuation));
    }

    [Fact]
    public void Join_OfAList_ShouldEqualThePairwiseJoin()
    {
        string?[] parts = ["They went home", null, ". You are so rude", "", "Why?", "  ", ", ok", "Done"];

        var expected = "";
        foreach (var part in parts)
            expected = UtteranceText.Join(expected, part);

        Assert.Equal("They went home. You are so rude Why?, ok Done", expected);
        Assert.Equal(expected, UtteranceText.Join(parts));
        Assert.Equal("", UtteranceText.Join([]));
    }
}
