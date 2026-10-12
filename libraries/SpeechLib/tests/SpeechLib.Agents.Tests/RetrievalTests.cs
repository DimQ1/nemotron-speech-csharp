using SpeechLib.Agents;
using Xunit;

namespace SpeechLib.Agents.Tests;

public class RetrievalTests
{
    private static readonly FakeLlmHost Host = new();

    [Fact]
    public void Chunk_ShouldProduceSegmentAlignedChunksInTokenRange()
    {
        var lines = Transcripts.English(repeat: 30);
        var chunks = TranscriptChunker.Chunk(lines, Host.CountTokens);

        Assert.True(chunks.Count > 3);
        foreach (var chunk in chunks)
            Assert.True(chunk.Tokens <= TranscriptChunker.DefaultMaxTokens, $"chunk {chunk.Index}: {chunk.Tokens}");
        foreach (var chunk in chunks.Take(chunks.Count - 1))
            Assert.InRange(chunk.Tokens, 300, TranscriptChunker.DefaultMaxTokens);

        // Aligned: concatenating the chunks gives back exactly the original segments, in order.
        var flat = chunks.SelectMany(c => c.Lines).ToList();
        Assert.Equal(lines.Count, flat.Count);
        for (var i = 0; i < lines.Count; i++)
            Assert.Same(lines[i], flat[i]);
    }

    [Fact]
    public void Chunk_ShouldSplitAnOversizedSegmentWithMonotonicTimes()
    {
        var sentence = "This is a rather long sentence about the quarterly numbers and the plan for next year. ";
        var huge = new TranscriptLine(100, 400, string.Concat(Enumerable.Repeat(sentence, 60)));
        var chunks = TranscriptChunker.Chunk([huge], Host.CountTokens);

        Assert.True(chunks.Count >= 2);
        var flat = chunks.SelectMany(c => c.Lines).ToList();
        Assert.All(chunks, c => Assert.True(c.Tokens <= TranscriptChunker.DefaultMaxTokens));
        Assert.Equal(100, flat[0].Start, 3);
        Assert.Equal(400, flat[^1].End, 3);
        for (var i = 1; i < flat.Count; i++)
            Assert.True(flat[i].Start >= flat[i - 1].Start);
    }

    [Fact]
    public void Search_ShouldRankTheRelevantEnglishChunkFirst()
    {
        var docs = new[]
        {
            "The hiring plan needs two more backend engineers before summer.",
            "Cloud costs are five percent over budget because of the analytics cluster.",
            "Support tickets about login errors doubled after the last release.",
        };
        var index = new Bm25Index(docs);

        Assert.Equal(1, index.Search("how much are the cloud costs over budget?", 3)[0]);
        Assert.Equal(2, index.Search("login error tickets", 3)[0]);
        Assert.Equal(0, index.Search("who are we hiring", 3)[0]);
        Assert.Empty(index.Search("zebra", 3));
    }

    [Fact]
    public void Search_ShouldRankTheRelevantRussianChunkFirstAndFoldYo()
    {
        var docs = Transcripts.Russian().Select(l => l.Text).ToList();
        var index = new Bm25Index(docs);

        Assert.Equal(1, index.Search("Когда сдвигается поставка?", 3)[0]);
        Assert.Equal(3, index.Search("что должен подтвердить Иван", 3)[0]);
        // "елки" (without the diaeresis) finds "Ёлки"; "приемку" finds "приёмку".
        Assert.Equal(2, index.Search("про елки", 3)[0]);
        Assert.Contains(5, index.Search("кто организует приемку", 3));
    }

    [Fact]
    public void TextTerms_ShouldStemAndDropStopWords()
    {
        Assert.Equal(TextTerms.Tokenize("meetings"), TextTerms.Tokenize("meeting"));
        Assert.DoesNotContain("the", TextTerms.Tokenize("the budget"));
        Assert.Equal(TextTerms.Tokenize("поставки"), TextTerms.Tokenize("поставка"));
        Assert.Equal(TextTerms.Tokenize("ёлка"), TextTerms.Tokenize("елка"));
    }

    [Fact]
    public async Task Builder_ShouldIncludeEverythingWhenTheTranscriptFitsTheBudget()
    {
        var builder = new TranscriptContextBuilder(Host.CountTokens, null);
        var source = Transcripts.Source("S1", "r1", Transcripts.English());

        var built = await builder.BuildAsync([source], "Who sends the forecast?", null, 1900, null, default);

        Assert.Equal(ContextMode.Full, built.Mode);
        Assert.Contains("[S1 00:18] Maria will send", built.Text);
        Assert.EndsWith("Question:", built.Text);
        Assert.True(built.Tokens <= 1900);
    }

    [Fact]
    public async Task Builder_ShouldRetrieveTheMatchingPassageFromALongTranscriptWithinBudget()
    {
        var builder = new TranscriptContextBuilder(Host.CountTokens, null);
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 40));

        var built = await builder.BuildAsync([source], "What is topic305 about?", null, 1500, null, default);

        Assert.Equal(ContextMode.Excerpts, built.Mode);
        Assert.Contains("topic305", built.Text);
        Assert.True(built.Tokens <= 1500 + 40, $"tokens {built.Tokens}");
    }

    [Fact]
    public async Task Builder_ShouldKeepEveryRecordingRepresentedAcrossSources()
    {
        var builder = new TranscriptContextBuilder(Host.CountTokens, null);
        var a = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 40));
        var b = Transcripts.Source("S2", "r2", Transcripts.English(repeat: 40));

        // Both sources contain the same words: the per-source cap stops one from taking every slot.
        var built = await builder.BuildAsync([a, b], "login errors tickets doubled", null, 1500, null, default);

        Assert.Contains("[S1 ", built.Text);
        Assert.Contains("[S2 ", built.Text);
    }

    [Fact]
    public async Task Builder_ShouldPickLinesByCueWordsForExtractionQuestions()
    {
        var builder = new TranscriptContextBuilder(Host.CountTokens, null);
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 40));

        var built = await builder.BuildAsync([source], ChatPrompts.QuickPrompts.First(p => p.Id == "actions").Prompt, null, 1500, null, default);

        Assert.Equal(ContextMode.Lines, built.Mode);
        Assert.Contains("will send the updated forecast", built.Text);
        Assert.True(built.Tokens <= 1500 + 40);
    }

    [Fact]
    public void Classify_ShouldRecognizeQuickPromptsAndFreeQuestions()
    {
        foreach (var quick in ChatPrompts.QuickPrompts)
            Assert.Equal(quick.Kind, ChatPrompts.Classify(quick.Prompt));
        Assert.Equal(QuestionKind.Overview, ChatPrompts.Classify("Can you summarize the meeting?"));
        Assert.Equal(QuestionKind.Overview, ChatPrompts.Classify("Расскажи кратко, о чем запись"));
        Assert.Equal(QuestionKind.Specific, ChatPrompts.Classify("When is the next meeting?"));
    }
}
