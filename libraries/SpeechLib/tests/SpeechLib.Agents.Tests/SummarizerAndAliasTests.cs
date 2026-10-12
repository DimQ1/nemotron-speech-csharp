using SpeechLib.Agents;
using SpeechLib.Llm;
using Xunit;

namespace SpeechLib.Agents.Tests;

public class SummarizerAndAliasTests
{
    [Fact]
    public async Task Builder_ShouldAnswerSummaryQuestionsFromTheDigestOfALongRecording()
    {
        var host = new FakeLlmHost { Responder = _ => "Part summary." };
        var summarizer = new MapReduceSummarizer(host, new InMemorySummaryStore());
        var builder = new TranscriptContextBuilder(host.CountTokens, summarizer);
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 80));
        var status = new List<string>();

        var built = await builder.BuildAsync([source], "Summarize this recording", null, 1500, new Progress<string>(status.Add), default);

        Assert.Equal(ContextMode.Digest, built.Mode);
        Assert.Contains("Summary of S1", built.Text);
        Assert.Contains("[S1 00:00] Part summary.", built.Text);
        Assert.True(built.Tokens <= 1500);
    }

    [Fact]
    public void Aliases_ShouldNeverBeReusedAndReAddingKeepsTheOldAlias()
    {
        var reg = new SourceAliasRegistry();
        Assert.Equal("S1", reg.Add("a"));
        Assert.Equal("S2", reg.Add("b"));
        reg.Remove("a");
        Assert.Equal("S3", reg.Add("c"));
        Assert.Equal("S1", reg.Add("a"));      // same recording: same alias
        Assert.Equal(["S1", "S2", "S3"], reg.ActiveEntries.Select(e => e.Alias));

        reg.Remove("b");
        Assert.Equal("S4", reg.Add("d"));
    }

    [Fact]
    public void Aliases_ShouldRebuildFromThePersistedEventLog()
    {
        var reg = new SourceAliasRegistry();
        reg.Apply(SourceEventKinds.Added, "S1", "a");
        reg.Apply(SourceEventKinds.Added, "S2", "b");
        reg.Apply(SourceEventKinds.Removed, "S2", "b");
        reg.Apply(SourceEventKinds.Added, "S3", "c");

        Assert.Equal("S4", reg.Add("d"));
        Assert.Equal(["a", "c", "d"], reg.ActiveEntries.Select(e => e.RecordingId));
        Assert.False(reg.Find("S2")!.Active);
    }

    [Fact]
    public void ContextBudget_ShouldFitTheWindowInTheWorstCase()
    {
        var budget = ContextBudget.ForWindow(4096);
        var worst = budget.AnswerTokens + budget.HistoryTokens + budget.MaxContextTokens + 400 /*system*/ + 100 /*question*/ + budget.OverheadTokens;
        Assert.True(worst <= 4096, $"worst case {worst}");
        Assert.Equal(1900, budget.ContextTokensFor(300, 50));
        Assert.True(budget.ContextTokensFor(300, 50) + 300 + 50 + budget.AnswerTokens + budget.HistoryTokens <= 4096);
        Assert.Equal(0, ContextBudget.ForWindow(1024).ContextTokensFor(5000, 5000));
    }
}
