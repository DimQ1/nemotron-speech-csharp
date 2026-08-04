using LVA.Core.StateMachine;
using Xunit;

namespace LVA.Tests.Core;

public class Unit_ConversationStateMachineTests
{
    [Fact]
    public void Activation_ShouldMoveIdleToContextCapture()
    {
        var sm = new ConversationStateMachine();
        Assert.Equal(ConversationState.Idle, sm.State);

        Assert.True(sm.OnActivation());
        Assert.Equal(ConversationState.ContextCapture, sm.State);
    }

    [Fact]
    public void DoubleActivation_ShouldBeIgnored()
    {
        var sm = new ConversationStateMachine();
        sm.OnActivation();
        Assert.False(sm.OnActivation());
        Assert.Equal(ConversationState.ContextCapture, sm.State);
    }

    [Fact]
    public void QuestionReady_OnlyFromContextCapture()
    {
        var sm = new ConversationStateMachine();
        Assert.False(sm.OnQuestionReady()); // from Idle — no-op

        sm.OnActivation();
        Assert.True(sm.OnQuestionReady());
        Assert.Equal(ConversationState.QuestionReady, sm.State);
    }

    [Fact]
    public void Cancel_ShouldResetToIdle()
    {
        var sm = new ConversationStateMachine();
        sm.OnActivation();
        sm.Reset("cancel");
        Assert.Equal(ConversationState.Idle, sm.State);
    }

    [Fact]
    public void Timeout_ShouldBeDetected()
    {
        var sm = new ConversationStateMachine(TimeSpan.FromSeconds(90));
        sm.OnActivation();
        Assert.False(sm.IsTimedOut(DateTimeOffset.UtcNow));
        Assert.True(sm.IsTimedOut(DateTimeOffset.UtcNow.AddSeconds(91)));
    }

    [Fact]
    public void EveryTransition_ShouldPublishEvent()
    {
        var sm = new ConversationStateMachine();
        var transitions = new List<(ConversationState, ConversationState, string)>();
        sm.Transitioned += (a, b, r) => transitions.Add((a, b, r));

        sm.OnActivation();
        sm.OnQuestionReady();
        sm.OnDispatched();

        Assert.Equal(3, transitions.Count);
        Assert.Equal((ConversationState.Idle, ConversationState.ContextCapture, "addressing"), transitions[0]);
        Assert.Equal((ConversationState.ContextCapture, ConversationState.QuestionReady, "question-ready"), transitions[1]);
        Assert.Equal((ConversationState.QuestionReady, ConversationState.Idle, "dispatched"), transitions[2]);
    }
}
