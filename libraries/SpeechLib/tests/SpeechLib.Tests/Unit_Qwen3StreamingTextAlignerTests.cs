using SpeechLib.Qwen3;
using Xunit;

namespace SpeechLib.Tests;

/// <summary>
/// Pins the Qwen3 streaming commit policy. The default accurate policy waits for a
/// second decode to agree with the first one; the opt-in <c>emitFirstBlock</c> policy
/// shows text at the first block boundary instead, which measured +3.5 pp WER on
/// CV17 because the append-only transcript cannot retract a wrong first hypothesis.
/// </summary>
public sealed class Unit_Qwen3StreamingTextAlignerTests
{
    [Fact]
    public void Push_FirstGrowingBlockWithholdsTextByDefault()
    {
        var aligner = new Qwen3StreamingTextAligner();

        Assert.Null(aligner.Push("one two three four", windowShifted: false));
        Assert.Equal("one two", aligner.Push("one two three four five", windowShifted: false));
    }

    [Fact]
    public void Push_FirstGrowingBlockEmitsImmediatelyWhenOptedIn()
    {
        var aligner = new Qwen3StreamingTextAligner(emitFirstBlock: true);

        // A 6-second clip produced no text until the final flush before this option.
        Assert.Equal("one two", aligner.Push("one two three four", windowShifted: false));
    }
    [Fact]
    public void Push_CommitsOnlyWordsBeforeConfirmedOverlap()
    {
        var aligner = new Qwen3StreamingTextAligner();

        Assert.Null(aligner.Push("one two three four"));
        Assert.Equal("one two", aligner.Push("three four five six"));
        Assert.Equal(" three four five six", aligner.Flush());
    }

    [Fact]
    public void Push_HoldsBoundaryWordsWhenTheModelChangesOverlap()
    {
        var aligner = new Qwen3StreamingTextAligner();

        Assert.Null(aligner.Push("alpha beta gamma delta"));
        Assert.Equal("alpha beta", aligner.Push("gamma changed epsilon"));
        Assert.Equal(" gamma changed epsilon", aligner.Flush());
    }

    [Fact]
    public void Push_GrowingWindowCommitsStablePrefixWithoutDuplicatingIt()
    {
        var aligner = new Qwen3StreamingTextAligner();

        Assert.Null(aligner.Push("one two", windowShifted: false));
        Assert.Null(aligner.Push("one two three four", windowShifted: false));
        Assert.Equal("one two", aligner.Push("one two three four five", windowShifted: false));
        Assert.Equal(" three four five", aligner.Flush());
    }

    [Fact]
    public void Push_TransitionsFromGrowingToShiftedWindow()
    {
        var aligner = new Qwen3StreamingTextAligner();

        Assert.Null(aligner.Push("one two three four", windowShifted: false));
        Assert.Equal("one two", aligner.Push("one two three four five", windowShifted: false));
        Assert.Null(aligner.Push("three four five six", windowShifted: true));
        Assert.Equal(" three four five six", aligner.Flush());
    }

    [Fact]
    public void Reset_DiscardsPendingWindow()
    {
        var aligner = new Qwen3StreamingTextAligner();

        aligner.Push("old text");
        aligner.Reset();

        Assert.Null(aligner.Flush());
    }
}