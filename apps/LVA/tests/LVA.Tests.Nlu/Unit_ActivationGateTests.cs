using LVA.Core.Configuration;
using LVA.Core.Intents;
using LVA.Nlu.Activation;
using Xunit;

namespace LVA.Tests.Nlu;

public class Unit_ActivationGateTests
{
    private static ActivationGate CreateGate(float threshold = 0.85f)
        => new(IntentTaxonomy.Default, new IntentEngineOptions
        {
            L1 = new IntentModelOptions { ThresholdAddressing = threshold }
        });

    [Fact]
    public void ShouldActivate_WhenBothSignalsPresent()
    {
        var gate = CreateGate();
        Assert.True(gate.IsActivated("ассистент, найди погоду", 0.90f));
    }

    [Fact]
    public void ShouldReject_WhenSemanticScoreBelowThreshold()
    {
        var gate = CreateGate();
        Assert.False(gate.IsActivated("ассистент, найди погоду", 0.80f));
    }

    [Fact]
    public void ShouldReject_WhenNoLexicalMatch()
    {
        var gate = CreateGate();
        // семантически похожий фон без ключевого слова
        Assert.False(gate.IsActivated("помоги мне с отчётом", 0.92f));
    }

    [Fact]
    public void LexicalMatch_ShouldBeCaseInsensitive()
    {
        var gate = CreateGate();
        Assert.True(gate.HasLexicalMatch("АССИСТЕНТ, слушай"));
        Assert.True(gate.HasLexicalMatch("Hey ASSISTANT"));
        Assert.False(gate.HasLexicalMatch("просто разговор"));
    }
}
