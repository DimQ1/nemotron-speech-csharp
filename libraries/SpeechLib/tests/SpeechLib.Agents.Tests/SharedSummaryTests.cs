using SpeechLib.Agents;
using SpeechLib.Llm;
using Xunit;

namespace SpeechLib.Agents.Tests;

/// <summary>The one summary pipeline: the job (Background) and chat (Interactive) share chunks and the final summary.</summary>
public class SharedSummaryTests
{
    [Fact]
    public async Task Digest_ShouldMapAndReduceWithSmallUnitsAndCacheEverything()
    {
        var host = new FakeLlmHost { Responder = _ => "Short summary of this part with names and numbers and a decision." };
        var store = new InMemorySummaryStore();
        var summarizer = new MapReduceSummarizer(host, store, LlmPriority.Background);
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 80));

        var digest = await summarizer.GetDigestAsync(source, 300, allowGenerate: true, null, default);

        Assert.NotNull(digest);
        Assert.True(digest!.Tokens <= 300, $"digest {digest.Tokens}");
        Assert.Equal(0, digest.Items[0].Start);
        Assert.All(host.Requests, r =>
        {
            Assert.Equal(LlmPriority.Background, r.Priority);
            Assert.True(r.Messages.Sum(m => host.CountTokens(m.Text)) <= 800, "unit too large for cheap preemption");
        });
        var chunks = store.OpenChunks(SummaryTarget.From(source), host.CountTokens);
        Assert.True(chunks.Count >= 8);
        Assert.All(chunks, c => Assert.NotNull(c.Summary));
        Assert.True(host.Requests.Count > chunks.Count); // map + at least one reduce
        Assert.NotNull(store.LoadSummary(SummaryTarget.From(source)));

        var before = host.Requests.Count;
        var again = await summarizer.GetDigestAsync(source, 300, allowGenerate: false, null, default);
        Assert.NotNull(again);
        Assert.Equal(before, host.Requests.Count);
        Assert.Equal(digest.Items.Count, again!.Items.Count);
    }

    [Fact]
    public async Task Digest_ShouldReturnNullWhenOnlyCachedSummariesAreAllowedAndNoneExist()
    {
        var host = new FakeLlmHost();
        var summarizer = new MapReduceSummarizer(host, new InMemorySummaryStore());
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 30));

        Assert.Null(await summarizer.GetDigestAsync(source, 300, allowGenerate: false, null, default));
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Digest_ShouldUseChunkSummariesFinishedByAnotherRunAndGenerateOnlyTheMissingOnes()
    {
        var host = new FakeLlmHost { Responder = _ => "A part summary [00:10]." };
        var store = new InMemorySummaryStore();
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 40));
        var target = SummaryTarget.From(source);
        var chunks = store.OpenChunks(target, host.CountTokens);
        Assert.True(chunks.Count >= 5);
        // The job got through the first two chunks.
        store.SaveChunkSummary(target, 0, "Job summary one [00:05].");
        store.SaveChunkSummary(target, 1, "Job summary two [03:00].");

        var status = new List<string>();
        var digest = await new MapReduceSummarizer(host, store).GetDigestAsync(source, 4000, allowGenerate: true, new SyncProgress(status.Add), default);

        Assert.Equal(chunks.Count - 2, host.Requests.Count);
        Assert.All(host.Requests, r => Assert.Equal(LlmPriority.Interactive, r.Priority));
        Assert.Equal($"Summarising part 3 of {chunks.Count}…", status[0]);
        Assert.Equal(chunks.Count, digest!.Items.Count);
        // Bare markers of the stored text become citable with the chat's alias.
        Assert.Contains("[S1 00:00] Job summary one [S1 00:05].", digest.Render("S1"));
    }

    [Fact]
    public async Task Digest_ShouldPreferTheStoredWholeSummaryWhenChunksDoNotFit()
    {
        var host = new FakeLlmHost { Responder = r => r.Messages[0].Text.StartsWith("You combine", StringComparison.Ordinal) ? "Whole recording summary." : new string('a', 300) };
        var store = new InMemorySummaryStore();
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 40));
        await new MapReduceSummarizer(host, store, LlmPriority.Background).EnsureAsync(SummaryTarget.From(source), null, default);
        var calls = host.Requests.Count;

        var digest = await new MapReduceSummarizer(host, store).GetDigestAsync(source, 200, allowGenerate: false, null, default);

        Assert.Equal(calls, host.Requests.Count);
        Assert.Single(digest!.Items);
        Assert.Equal("Whole recording summary.", digest.Items[0].Text);
    }

    [Fact]
    public async Task Ensure_ShouldResumeAfterCancellationWithoutRepeatingFinishedChunks()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeLlmHost();
        host.Responder = _ =>
        {
            if (host.Requests.Count == 3)
                cts.Cancel();
            return "A summary.";
        };
        var store = new InMemorySummaryStore();
        var source = Transcripts.Source("S1", "r1", Transcripts.English(repeat: 40));
        var target = SummaryTarget.From(source);
        var summarizer = new MapReduceSummarizer(host, store, LlmPriority.Background);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => summarizer.EnsureAsync(target, null, cts.Token));
        var done = store.OpenChunks(target, host.CountTokens).Count(c => c.Summary is not null);
        Assert.InRange(done, 2, 3);
        var before = host.Requests.Count;

        await summarizer.EnsureAsync(target, null, default);

        var total = store.OpenChunks(target, host.CountTokens).Count;
        Assert.Equal(total - done + 1, host.Requests.Count - before); // the remaining chunks + one reduce
        Assert.NotNull(store.LoadSummary(target));
    }

    [Fact]
    public async Task Store_ShouldDiscardSummariesOfAChangedTranscript()
    {
        var host = new FakeLlmHost();
        var store = new InMemorySummaryStore();
        var lines = Transcripts.English(repeat: 20);
        var target = new SummaryTarget("r1", "v1", () => lines);
        await new MapReduceSummarizer(host, store).EnsureAsync(target, null, default);
        Assert.NotNull(store.LoadSummary(target));

        lines = Transcripts.English(repeat: 20);
        lines[3] = lines[3] with { Text = "Completely different sentence." };

        Assert.All(store.OpenChunks(target, host.CountTokens), c => Assert.Null(c.Summary));
        Assert.Null(store.LoadSummary(target));
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
