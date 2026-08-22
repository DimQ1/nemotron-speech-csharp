using System.Text;
using SpeechLib.Translation;
using Xunit;

namespace VoiceType.Tests;

public sealed class Unit_SentenceSplitterTests
{
    [Fact]
    public void Extract_WhenTerminatorFollowedByWhitespace_YieldsTrimmedSentence()
    {
        var buffer = new StringBuilder("Hello world. ");
        var consumed = 0;

        var sentences = SentenceSplitter.ExtractCompleteSentences(buffer, ref consumed);

        Assert.Single(sentences);
        Assert.Equal("Hello world.", sentences[0]);
        Assert.Equal(buffer.Length, consumed);
    }

    [Fact]
    public void Extract_WhenMultipleSentences_YieldsEach()
    {
        var buffer = new StringBuilder("First. Second! Third?");
        var consumed = 0;

        var sentences = SentenceSplitter.ExtractCompleteSentences(buffer, ref consumed);

        Assert.Equal(3, sentences.Count);
        Assert.Equal("First.", sentences[0]);
        Assert.Equal("Second!", sentences[1]);
        Assert.Equal("Third?", sentences[2]);
        Assert.Equal(buffer.Length, consumed);
    }

    [Fact]
    public void Extract_LeavesIncompleteTailForNextCall()
    {
        var buffer = new StringBuilder("Done. Incomplete");
        var consumed = 0;

        var first = SentenceSplitter.ExtractCompleteSentences(buffer, ref consumed);

        Assert.Single(first);
        Assert.Equal("Done.", first[0]);
        Assert.Equal(6, consumed); // "Done. " — the trailing space is consumed too

        buffer.Append(" tail.");
        var second = SentenceSplitter.ExtractCompleteSentences(buffer, ref consumed);

        Assert.Single(second);
        Assert.Equal("Incomplete tail.", second[0]);
        Assert.Equal(buffer.Length, consumed);
    }

    [Fact]
    public void Extract_DoesNotSplitDecimalNumbers()
    {
        var buffer = new StringBuilder("Pi is 3.14 today. ");
        var consumed = 0;

        var sentences = SentenceSplitter.ExtractCompleteSentences(buffer, ref consumed);

        Assert.Single(sentences);
        Assert.Equal("Pi is 3.14 today.", sentences[0]);
        Assert.Equal(buffer.Length, consumed);
    }

    [Fact]
    public void Extract_MultiTerminator_TreatedAsOneSentence()
    {
        var buffer = new StringBuilder("Really?! ");
        var consumed = 0;

        var sentences = SentenceSplitter.ExtractCompleteSentences(buffer, ref consumed);

        Assert.Single(sentences);
        Assert.Equal("Really?!", sentences[0]);
        Assert.Equal(buffer.Length, consumed);
    }

    [Fact]
    public void Extract_EmptyBuffer_ReturnsEmpty()
    {
        var buffer = new StringBuilder();
        var consumed = 0;

        var sentences = SentenceSplitter.ExtractCompleteSentences(buffer, ref consumed);

        Assert.Empty(sentences);
        Assert.Equal(0, consumed);
    }
}
