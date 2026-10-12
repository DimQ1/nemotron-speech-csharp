using SpeechLib.Agents;
using SpeechLib.Llm;
using Xunit;

namespace SpeechLib.Agents.Tests;

public class AgentTests
{
    private static async Task<string> AskAsync(TranscriptChatAgent agent, TranscriptChatSession session, string question, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var piece in agent.RunStreamingAsync(session, question, null, ct))
            sb.Append(piece);
        return sb.ToString();
    }

    private static (FakeLlmHost Host, FakeCatalog Catalog, TranscriptChatAgent Agent) Create(int englishRepeat = 1)
    {
        var host = new FakeLlmHost { Responder = r => "The forecast goes to finance [S1 00:18]." };
        var catalog = new FakeCatalog();
        catalog.Sources.Add(Transcripts.Source("S1", "r1", Transcripts.English(englishRepeat)));
        return (host, catalog, new TranscriptChatAgent(host, catalog, "chat1"));
    }

    [Fact]
    public async Task Run_ShouldSendSystemRulesAndTranscriptContextAndStreamTheAnswer()
    {
        var (host, _, agent) = Create();
        var session = await agent.CreateSessionAsync();

        var answer = await AskAsync(agent, session, "Who sends the forecast?");

        Assert.Equal("The forecast goes to finance [S1 00:18].", answer);
        var request = Assert.Single(host.Requests);
        Assert.Equal(LlmPriority.Interactive, request.Priority);
        Assert.Equal("chat1", request.ConversationKey);
        Assert.Equal(LlmRole.System, request.Messages[0].Role);
        Assert.Contains("[S1] \"Recording S1\"", request.Messages[0].Text);
        var last = request.Messages[^1];
        Assert.Equal(LlmRole.User, last.Role);
        Assert.Contains("[S1 00:18] Maria will send", last.Text);
        Assert.EndsWith("Who sends the forecast?", last.Text);
        Assert.Equal(1, request.Messages.Count(m => m.Role == LlmRole.System));
    }

    [Fact]
    public async Task Session_ShouldNeverStoreTranscriptsInTheHistory()
    {
        var (_, _, agent) = Create();
        var session = await agent.CreateSessionAsync();
        await AskAsync(agent, session, "Who sends the forecast?");

        var json = (await agent.SerializeAsync(session)).GetRawText();

        Assert.DoesNotContain("Maria will send", json);
        Assert.DoesNotContain("Transcript:", json);
        Assert.Contains("Who sends the forecast?", json);
        Assert.Contains("The forecast goes to finance", json);
        var history = agent.GetHistory(session);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].IsUser);
    }

    [Fact]
    public async Task Session_ShouldSurviveARestartAndKeepTheHistory()
    {
        var (host, catalog, agent) = Create();
        var session = await agent.CreateSessionAsync();
        await AskAsync(agent, session, "Who sends the forecast?");
        var stored = await agent.SerializeAsync(session);
        agent.Dispose();

        // "Restart": a new agent over the same persisted blob.
        var restarted = new TranscriptChatAgent(host, catalog, "chat1");
        var restored = await restarted.TryDeserializeAsync(stored);
        Assert.NotNull(restored);
        await AskAsync(restarted, restored!, "And when?");

        var request = host.Requests[^1];
        var turns = request.Messages.Where(m => m.Role != LlmRole.System).ToList();
        Assert.Equal(3, turns.Count);
        Assert.Equal("Who sends the forecast?", turns[0].Text);
        Assert.StartsWith("The forecast goes to finance", turns[1].Text);
        Assert.EndsWith("And when?", turns[2].Text);
    }

    [Fact]
    public async Task Session_ShouldBeRebuiltFromTheDisplayLogWhenTheStoredBlobIsUnreadable()
    {
        var (host, _, agent) = Create();
        using var junk = System.Text.Json.JsonDocument.Parse("""{"someFutureFormat":[1,2,3],"stateBag":42}""");

        var restored = await agent.TryDeserializeAsync(junk.RootElement.Clone());
        var session = restored ?? await agent.RebuildSessionAsync(
        [
            new ChatTurn(true, "Who sends the forecast?"),
            new ChatTurn(false, "Maria [S1 00:18]."),
        ]);
        await AskAsync(agent, session, "By when?");

        var turns = host.Requests[^1].Messages.Where(m => m.Role != LlmRole.System).ToList();
        Assert.Equal(3, turns.Count);
        Assert.Equal("Maria [S1 00:18].", turns[1].Text);
    }

    [Fact]
    public async Task Sources_ShouldChangeBetweenTurnsWithoutTouchingTheSession()
    {
        var (host, catalog, agent) = Create();
        var session = await agent.CreateSessionAsync();
        await AskAsync(agent, session, "Who sends the forecast?");

        catalog.Sources.Add(Transcripts.Source("S2", "r2", Transcripts.Russian(), title: "Звонок с поставщиком"));
        await AskAsync(agent, session, "Когда сдвигается поставка?");
        var withBoth = host.Requests[^1];
        Assert.Contains("[S2] \"Звонок с поставщиком\"", withBoth.Messages[0].Text);
        Assert.Contains("[S2 00:08]", withBoth.Messages[^1].Text);
        Assert.Contains("[S1 00:18]", withBoth.Messages[^1].Text);

        catalog.Sources[0] = Transcripts.Source("S1", "r1", Transcripts.English(), active: false);
        await AskAsync(agent, session, "Who is responsible for the intake?");
        var afterRemoval = host.Requests[^1];
        Assert.Contains("S1 was removed by the user", afterRemoval.Messages[0].Text);
        Assert.DoesNotContain("[S1 00:18]", afterRemoval.Messages[^1].Text);
        Assert.Contains("[S2 ", afterRemoval.Messages[^1].Text);

        // The history carried over all three turns; transcripts are still not in it.
        Assert.Equal(6, agent.GetHistory(session).Count);
        Assert.DoesNotContain("Maria will send", (await agent.SerializeAsync(session)).GetRawText());
    }

    [Fact]
    public async Task Run_ShouldNeverExceedTheWindowEvenWithAHugeHistoryAndTranscript()
    {
        var (host, _, agent) = Create(englishRepeat: 200);
        host.Responder = _ => string.Join(' ', Enumerable.Repeat("word", 150));
        var session = await agent.CreateSessionAsync();

        for (var i = 0; i < 12; i++)
            await AskAsync(agent, session, $"Question number {i}: what is topic{i * 7} about, please explain in detail?");

        foreach (var request in host.Requests)
        {
            var prompt = request.Messages.Sum(m => host.CountTokens(m.Text) + 6);
            var budget = ContextBudget.ForWindow(4096);
            Assert.True(budget.TotalFor(prompt) <= 4096, $"prompt {prompt} + answer exceeds the window");
        }
        Assert.Equal(LlmRole.User, host.Requests[^1].Messages.First(m => m.Role != LlmRole.System).Role);
    }

    [Fact]
    public void ContextBudget_ShouldGrowWithTheWindowAndStillFit()
    {
        var small = ContextBudget.ForWindow(4096);
        var large = ContextBudget.ForWindow(8192);

        Assert.True(large.MaxContextTokens > small.MaxContextTokens);
        Assert.True(large.HistoryTokens > small.HistoryTokens);
        Assert.True(large.AnswerTokens >= small.AnswerTokens);
        var worst = large.AnswerTokens + large.HistoryTokens + large.MaxContextTokens + 400 + 100 + large.OverheadTokens;
        Assert.True(worst <= 8192, $"worst case {worst} exceeds 8192");
    }

    [Fact]
    public async Task Run_ShouldFollowTheHostContextWindowPerTurn()
    {
        var (host, _, agent) = Create(englishRepeat: 200);
        var session = await agent.CreateSessionAsync();

        await AskAsync(agent, session, "What about topic7 and the hiring plan?");
        var smallExcerpt = host.Requests[^1].Messages[^1].Text.Length;

        host.ContextTokensValue = 8192; // the user switched to a bigger model between two turns
        await AskAsync(agent, session, "What about topic7 and the hiring plan?");
        var largeExcerpt = host.Requests[^1].Messages[^1].Text.Length;

        Assert.True(largeExcerpt > smallExcerpt * 1.5, $"excerpt {smallExcerpt} -> {largeExcerpt} did not grow with the window");
        foreach (var request in host.Requests)
        {
            var prompt = request.Messages.Sum(m => host.CountTokens(m.Text) + 6);
            Assert.True(ContextBudget.ForWindow(host.ContextTokensValue).TotalFor(prompt) <= 8192);
        }
    }

    [Fact]
    public async Task Run_ShouldKeepThePartialTextWhenCancelled()
    {
        var (host, _, agent) = Create();
        host.Responder = _ => "First part of the answer then more words here";
        host.HangAfterPieces = 4;
        var session = await agent.CreateSessionAsync();
        using var cts = new CancellationTokenSource();

        var partial = new System.Text.StringBuilder();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var piece in agent.RunStreamingAsync(session, "Question?", null, cts.Token))
            {
                partial.Append(piece);
                if (partial.Length >= 10)
                    cts.Cancel();
            }
        });

        Assert.StartsWith("First part", partial.ToString());
        // A cancelled run leaves no half turn in the session: the caller rebuilds from its display log.
        var rebuilt = await agent.RebuildSessionAsync([new ChatTurn(true, "Question?"), new ChatTurn(false, partial.ToString())]);
        Assert.Equal(2, agent.GetHistory(rebuilt).Count);
    }

    [Fact]
    public async Task Run_ShouldSurfaceAnUnavailableModel()
    {
        var (host, _, agent) = Create();
        host.Installed = false;
        var session = await agent.CreateSessionAsync();

        await Assert.ThrowsAsync<LlmUnavailableException>(() => AskAsync(agent, session, "Anything?"));
    }

    [Fact]
    public async Task Dispose_ShouldReleaseTheHostConversation()
    {
        var (host, _, agent) = Create();
        agent.Dispose();

        Assert.Equal(["chat1"], host.Released);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task LlmChatClient_ShouldMapInstructionsRolesAndSampling()
    {
        var host = new FakeLlmHost { Responder = _ => "hi there" };
        var client = new LlmChatClient(host, "k", LlmPriority.Background);
        var options = new Microsoft.Extensions.AI.ChatOptions { Instructions = "Be brief.", Temperature = 0.3f, TopP = 0.9f, MaxOutputTokens = 50 };

        var response = await client.GetResponseAsync(
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Hello"),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "Hi"),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "More"),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "context"),
        ], options);

        Assert.Equal("hi there", response.Text);
        var req = Assert.Single(host.Requests);
        Assert.Equal(LlmPriority.Background, req.Priority);
        Assert.Equal("k", req.ConversationKey);
        Assert.Equal([LlmRole.System, LlmRole.User, LlmRole.Assistant, LlmRole.User], req.Messages.Select(m => m.Role));
        Assert.Equal("Be brief.", req.Messages[0].Text);
        Assert.Equal("More\n\ncontext", req.Messages[3].Text);
        Assert.Equal(50, req.Sampling!.MaxOutputTokens);
        Assert.Equal(0.3, req.Sampling.Temperature!.Value, 3);
    }
}
