using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native.Tests;

public sealed class Unit_GemmaConversationHistoryTests
{
    private static GemmaConversationSpec Spec(IReadOnlyList<LlmMessage>? history = null) =>
        new("Keep the request's details.", history ?? [], 128, null);

    [Fact]
    public void InterruptedTurn_ShouldKeepItsRequestBeforeThePartialAnswer()
    {
        var log = new GemmaConversationHistory(Spec());
        log.BeginTurn("Explain why the code word is TANGERINE.");

        var snapshot = log.Snapshot("The code word ");

        Assert.Equal(
            [new LlmMessage(LlmRole.User, "Explain why the code word is TANGERINE."),
             new LlmMessage(LlmRole.Assistant, "The code word ")],
            snapshot.Turns);
        Assert.Equal("Keep the request's details.", snapshot.Spec.System);
    }

    [Fact]
    public void Snapshot_ShouldNotMutateHistoryOrRepeatPartialTextOnAnotherCapture()
    {
        var history = new List<LlmMessage> { new(LlmRole.User, "Remember Miso."), new(LlmRole.Assistant, "OK") };
        var log = new GemmaConversationHistory(Spec(history));
        history.Clear();
        log.BeginTurn("Describe Miso.");

        var first = log.Snapshot("A cat");
        var second = log.Snapshot("A cat called Miso");

        Assert.Equal(4, first.Turns.Count);
        Assert.Equal(4, second.Turns.Count);
        Assert.Equal("A cat", first.Turns[^1].Text);
        Assert.Equal("A cat called Miso", second.Turns[^1].Text);
        Assert.Equal("Remember Miso.", first.Turns[0].Text);
    }

    [Fact]
    public void ResumedTurn_ShouldKeepTheWholeChainAcrossTwoInterruptions()
    {
        var firstLog = new GemmaConversationHistory(Spec());
        firstLog.BeginTurn("Count from 400.");
        var first = firstLog.Snapshot("400, 401, ");
        var resumed = new GemmaConversationHistory(first.Spec with { History = first.Turns });
        resumed.BeginTurn("Continue.");

        var second = resumed.Snapshot("402, 403, ");

        Assert.Equal(
            [new LlmMessage(LlmRole.User, "Count from 400."),
             new LlmMessage(LlmRole.Assistant, "400, 401, "),
             new LlmMessage(LlmRole.User, "Continue."),
             new LlmMessage(LlmRole.Assistant, "402, 403, ")],
            second.Turns);
    }

    [Fact]
    public void CompletedTurn_ShouldBeRecordedOnceWithoutAPartialAnswer()
    {
        var log = new GemmaConversationHistory(Spec());
        log.BeginTurn("Hi.");
        log.CompleteTurn("Hello.");

        Assert.Equal(2, log.Snapshot(null).Turns.Count);
        Assert.Throws<InvalidOperationException>(() => log.Snapshot("Hello."));
    }
}
