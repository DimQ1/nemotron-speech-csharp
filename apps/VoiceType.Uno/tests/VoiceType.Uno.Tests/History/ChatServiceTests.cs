using System.Runtime.CompilerServices;
using System.Text.Json;
using SpeechLib.Llm;
using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class ChatServiceTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly RecordingStore _recordings;
    private readonly ChatStore _chats;
    private readonly ScriptedHost _host = new();
    private ChatService _service;

    public ChatServiceTests()
    {
        _recordings = new RecordingStore(_directory.CreateSubdirectory("sessions"));
        _chats = new ChatStore(Path.Combine(_directory.Path, "chats"));
        _service = new ChatService(_recordings, _chats, _host);
    }

    public void Dispose() => _directory.Dispose();

    private string AddRecording(string title, params (double Start, double End, string Text)[] segments)
    {
        var entry = _recordings.CreateRecording(HistoryFixtures.T0);
        _recordings.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript(segments));
        _recordings.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(1));
        _recordings.Rename(entry.Id, title);
        return entry.Id;
    }

    private string Meeting() => AddRecording("Weekly sync",
        (0, 8, "Welcome everyone to the weekly sync about the budget."),
        (8, 16, "Cloud costs are five percent over budget this quarter."),
        (16, 24, "Maria will send the updated forecast to finance by Friday."),
        (24, 32, "We decided to move the next meeting to Monday."));

    private string Supplier() => AddRecording("Звонок с поставщиком",
        (0, 8, "Поставка сдвигается на две недели из-за таможни."),
        (8, 16, "Иван должен подтвердить новую дату до среды."));

    private static async Task<List<ChatEvent>> CollectAsync(IAsyncEnumerable<ChatEvent> stream)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in stream)
            list.Add(e);
        return list;
    }

    [Fact]
    public void CreateChat_ShouldPinCurrentVersionsAndLogStableAliases()
    {
        var a = Meeting();
        var b = Supplier();

        var chat = _service.CreateChat([a, b], "Two calls");

        Assert.Equal(["v1", "v1"], chat.Sources.Select(s => s.VersionId));
        var aliases = _service.GetAliases(chat.Id);
        Assert.Equal("S1", aliases[a]);
        Assert.Equal("S2", aliases[b]);
        var events = _chats.Get(chat.Id)!.Messages.Where(m => m.Role == ChatRoles.System).ToList();
        Assert.Equal(2, events.Count);
        Assert.Contains("Weekly sync", events[0].Text);
    }

    [Fact]
    public void CreateChat_ShouldRejectRecordingsWithoutTranscript()
    {
        Assert.Throws<ArgumentException>(() => _service.CreateChat(["nope00"]));
    }

    [Fact]
    public async Task Send_ShouldSaveTheQuestionFirstStreamAndSaveTheAnswerWithValidCitations()
    {
        var chat = _service.CreateChat([Meeting()]);
        _host.Responder = _ => "Maria sends the forecast [S1 00:18] by Friday [S1 09:59] [S7 00:01].";

        var events = await CollectAsync(_service.SendAsync(chat.Id, "Who sends the forecast?"));

        Assert.IsType<ChatEvent.Status>(events[0]);
        Assert.Contains(events, e => e is ChatEvent.Delta);
        var completed = Assert.IsType<ChatEvent.Completed>(events[^1]);
        Assert.False(completed.Message.Incomplete);
        Assert.Equal("Maria sends the forecast [S1 00:16] by Friday.", completed.Message.Text);
        var citation = Assert.Single(completed.Message.Citations);
        Assert.Equal(16, citation.StartSec);
        Assert.Equal("v1", citation.VersionId);

        var saved = _chats.Get(chat.Id)!;
        var talk = saved.Messages.Where(m => m.Role != ChatRoles.System).ToList();
        Assert.Equal([ChatRoles.User, ChatRoles.Assistant], talk.Select(m => m.Role));
        Assert.Equal("Who sends the forecast?", talk[0].Text);
        Assert.NotNull(saved.AgentSession);
        // Transcripts never land in the stored session.
        Assert.DoesNotContain("Maria will send", saved.AgentSession!.Value.GetRawText());
    }

    [Fact]
    public async Task Send_ShouldSaveTheQuestionBeforeTheEngineAnswers()
    {
        var chat = _service.CreateChat([Meeting()]);
        string[]? seenWhileGenerating = null;
        _host.Responder = _ =>
        {
            seenWhileGenerating = _chats.Get(chat.Id)!.Messages.Where(m => m.Role == ChatRoles.User).Select(m => m.Text).ToArray();
            return "Fine.";
        };

        await CollectAsync(_service.SendAsync(chat.Id, "Anything?"));

        Assert.Equal(new[] { "Anything?" }, seenWhileGenerating);
    }

    [Fact]
    public async Task Send_ShouldKeepThePartialTextMarkedIncompleteWhenCancelled()
    {
        var chat = _service.CreateChat([Meeting()]);
        _host.Responder = _ => "Costs are over budget by five percent and then it goes on and on";
        _host.HangAfterPieces = 4;
        using var cts = new CancellationTokenSource();

        var events = new List<ChatEvent>();
        await foreach (var e in _service.SendAsync(chat.Id, "Costs?", cts.Token))
        {
            events.Add(e);
            if (e is ChatEvent.Delta && events.OfType<ChatEvent.Delta>().Count() == 3)
                cts.Cancel();
        }

        var completed = Assert.IsType<ChatEvent.Completed>(events[^1]);
        Assert.True(completed.Message.Incomplete);
        Assert.StartsWith("Costs are over", completed.Message.Text);
        var saved = _chats.Get(chat.Id)!.Messages.Last();
        Assert.True(saved.Incomplete);
        Assert.Equal(ChatRoles.Assistant, saved.Role);

        // The next turn still works and sees the partial answer in its history.
        _host.HangAfterPieces = null;
        _host.Responder = _ => "Next answer.";
        await CollectAsync(_service.SendAsync(chat.Id, "And then?"));
        var turns = _host.Requests[^1].Messages.Where(m => m.Role != LlmRole.System).ToList();
        Assert.Equal(3, turns.Count);
        Assert.StartsWith("Costs are over", turns[1].Text);
    }

    [Fact]
    public async Task Send_ShouldFailCleanlyAndSaveNothingWhenTheModelIsNotInstalled()
    {
        var chat = _service.CreateChat([Meeting()]);
        _host.Installed = false;

        Assert.False(_service.IsAvailable);
        var events = await CollectAsync(_service.SendAsync(chat.Id, "Hello?"));

        var failed = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Contains("not installed", failed.Error);
        Assert.DoesNotContain(_chats.Get(chat.Id)!.Messages, m => m.Role == ChatRoles.User);
    }

    [Fact]
    public async Task Send_ShouldReportAnEngineFailureAndKeepPartialText()
    {
        var chat = _service.CreateChat([Meeting()]);
        _host.Responder = _ => "Some words before the failure";
        _host.FailAfterPieces = 3;

        var events = await CollectAsync(_service.SendAsync(chat.Id, "Q?"));

        Assert.Contains(events, e => e is ChatEvent.Failed);
        var completed = Assert.IsType<ChatEvent.Completed>(events[^1]);
        Assert.True(completed.Message.Incomplete);
    }

    [Fact]
    public async Task Sources_ShouldChangeMidConversationWithStableAliases()
    {
        var a = Meeting();
        var b = Supplier();
        var c = AddRecording("Third call", (0, 5, "Hello from the third call."));
        var chat = _service.CreateChat([a]);
        await CollectAsync(_service.SendAsync(chat.Id, "Who sends the forecast?"));

        chat = _service.AddSource(chat.Id, b);
        Assert.Equal("S2", _service.GetAliases(chat.Id)[b]);
        await CollectAsync(_service.SendAsync(chat.Id, "Когда сдвигается поставка?"));
        var system = _host.Requests[^1].Messages[0].Text;
        Assert.Contains("[S1] \"Weekly sync\"", system);
        Assert.Contains("[S2] \"Звонок с поставщиком\"", system);
        Assert.Equal(4, _host.Requests[^1].Messages.Count); // system, earlier question, earlier answer, context + question
        Assert.Equal(1, _host.Requests[^1].Messages.Count(m => m.Role == LlmRole.Assistant));

        chat = _service.RemoveSource(chat.Id, a);
        await CollectAsync(_service.SendAsync(chat.Id, "And now?"));
        Assert.Contains("S1 was removed by the user", _host.Requests[^1].Messages[0].Text);

        // S1 and S2 are never reused: the next recording is S3; re-adding the first one gets S1 back.
        chat = _service.AddSource(chat.Id, c);
        Assert.Equal("S3", _service.GetAliases(chat.Id)[c]);
        chat = _service.AddSource(chat.Id, a);
        Assert.Equal("S1", _service.GetAliases(chat.Id)[a]);

        // The log shows what changed, in order.
        var events = _chats.Get(chat.Id)!.Messages.Where(m => m.Role == ChatRoles.System).Select(m => m.Text).ToList();
        Assert.Equal(5, events.Count);
        Assert.Contains("Removed", events[2]);

        Assert.Throws<InvalidOperationException>(() =>
        {
            var solo = _service.CreateChat([a]);
            _service.RemoveSource(solo.Id, a);
        });
    }

    [Fact]
    public async Task Session_ShouldSurviveARestartThroughTheStoredBlob()
    {
        var chat = _service.CreateChat([Meeting()]);
        await CollectAsync(_service.SendAsync(chat.Id, "Who sends the forecast?"));
        _service.CloseChat(chat.Id);
        Assert.Contains(chat.Id, _host.Released);

        // "Restart": a new service (and agent) over the same stores.
        _service = new ChatService(_recordings, _chats, _host);
        await CollectAsync(_service.SendAsync(chat.Id, "By when?"));

        var turns = _host.Requests[^1].Messages.Where(m => m.Role != LlmRole.System).ToList();
        Assert.Equal(3, turns.Count);
        Assert.Equal("Who sends the forecast?", turns[0].Text);
    }

    [Fact]
    public async Task Session_ShouldBeRebuiltFromTheDisplayLogWhenTheStoredBlobIsUnreadable()
    {
        var chat = _service.CreateChat([Meeting()]);
        await CollectAsync(_service.SendAsync(chat.Id, "Who sends the forecast?"));
        _service.CloseChat(chat.Id);

        using var junk = JsonDocument.Parse("""{"format":"from-the-future","x":[1,2]}""");
        _chats.SaveAgentSession(chat.Id, junk.RootElement.Clone());
        _service = new ChatService(_recordings, _chats, _host);

        var events = await CollectAsync(_service.SendAsync(chat.Id, "By when?"));

        Assert.IsType<ChatEvent.Completed>(events[^1]);
        var turns = _host.Requests[^1].Messages.Where(m => m.Role != LlmRole.System).ToList();
        Assert.Equal(3, turns.Count);
        Assert.Equal("Who sends the forecast?", turns[0].Text);
    }

    [Fact]
    public async Task Session_ShouldBeRebuiltWhenItDisagreesWithTheLog()
    {
        var chat = _service.CreateChat([Meeting()]);
        await CollectAsync(_service.SendAsync(chat.Id, "First?"));
        var staleSession = _chats.Get(chat.Id)!.AgentSession;
        await CollectAsync(_service.SendAsync(chat.Id, "Second?"));
        _service.CloseChat(chat.Id);
        _chats.SaveAgentSession(chat.Id, staleSession); // crash between the log write and the session write
        _service = new ChatService(_recordings, _chats, _host);

        await CollectAsync(_service.SendAsync(chat.Id, "Third?"));

        var turns = _host.Requests[^1].Messages.Where(m => m.Role != LlmRole.System).ToList();
        Assert.Equal(5, turns.Count);
    }

    [Fact]
    public void GetOutdatedSources_ShouldFlagANewerVersionAndUseCurrentVersionShouldMoveIt()
    {
        var a = Meeting();
        var chat = _service.CreateChat([a]);
        Assert.Empty(_service.GetOutdatedSources(chat.Id));

        _recordings.AddVersion(a, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript((0, 5, "A newer transcript.")));
        var outdated = Assert.Single(_service.GetOutdatedSources(chat.Id));
        Assert.Equal("v1", outdated.ChatVersionId);
        Assert.Equal("v2", outdated.CurrentVersionId);

        var moved = _service.UseCurrentVersion(chat.Id, a);
        Assert.Equal("v2", moved.Sources[0].VersionId);
        Assert.Equal("S1", _service.GetAliases(chat.Id)[a]);
        Assert.Empty(_service.GetOutdatedSources(chat.Id));
        Assert.Contains("v2", _chats.Get(chat.Id)!.Messages.Last().Text);
    }

    [Fact]
    public async Task Send_ShouldAnswerFromTheChatsOwnVersionNotTheCurrentOne()
    {
        var a = Meeting();
        var chat = _service.CreateChat([a]);
        _recordings.AddVersion(a, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript((0, 5, "Completely different words.")));

        await CollectAsync(_service.SendAsync(chat.Id, "Who sends the forecast?"));

        Assert.Contains("Maria will send", _host.Requests[^1].Messages[^1].Text);
        Assert.DoesNotContain("Completely different", _host.Requests[^1].Messages[^1].Text);
    }

    [Fact]
    public void QuickPrompts_ShouldCoverTheFiveStandardQuestions()
    {
        Assert.Equal(["summary", "actions", "decisions", "questions", "speakers"], _service.QuickPrompts.Select(p => p.Id));
        Assert.All(_service.QuickPrompts, p => Assert.False(string.IsNullOrWhiteSpace(p.Prompt)));
    }

    [Fact]
    public async Task Summaries_ShouldBeCachedNextToTheRecording()
    {
        var segments = Enumerable.Range(0, 120)
            .Select(i => ((double)i * 8, (double)i * 8 + 7.5, $"Sentence number {i} about the quarterly budget and hiring plan topic{i}."))
            .ToArray();
        var id = AddRecording("Long one", segments);
        var chat = _service.CreateChat([id]);
        _host.Responder = r => r.Messages.Count == 1 ? "Short summary." : "Final answer [S1 00:00].";

        await CollectAsync(_service.SendAsync(chat.Id, "Summarize this recording"));

        var cache = Path.Combine(_recordings.TryGetFolder(id)!, "analysis", "v1.chunks.json");
        Assert.True(File.Exists(cache));
        var calls = _host.Requests.Count;

        await CollectAsync(_service.SendAsync(chat.Id, "Summarize this recording once more"));
        Assert.Equal(calls + 1, _host.Requests.Count); // only the answer: every summary came from the cache
    }

    private sealed class ScriptedHost : ILlmHost
    {
        public List<LlmRequest> Requests { get; } = [];
        public Func<LlmRequest, string> Responder { get; set; } = _ => "OK.";
        public bool Installed { get; set; } = true;
        public int? HangAfterPieces { get; set; }
        public int? FailAfterPieces { get; set; }
        public List<string> Released { get; } = [];

        public bool IsModelInstalled => Installed;
        public int ContextTokens => 4096;
        public LlmHostStatus Status => new(LlmHostState.Idle, null, 0);
        public event Action<LlmHostStatus>? StatusChanged { add { } remove { } }
        public int CountTokens(string text) => (int)Math.Ceiling(text.Length / 3.2);
        public void ReleaseConversation(string conversationKey) => Released.Add(conversationKey);

        public async IAsyncEnumerable<string> GenerateStreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!Installed)
                throw new LlmUnavailableException("The Gemma model is not installed.");
            lock (Requests)
                Requests.Add(request);
            var words = Responder(request).Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HangAfterPieces is { } hang && i >= hang)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                if (FailAfterPieces is { } fail && i >= fail)
                    throw new InvalidOperationException("engine failure");
                await Task.Yield();
                yield return i == 0 ? words[i] : " " + words[i];
            }
        }

        public async Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var piece in GenerateStreamAsync(request, cancellationToken))
                sb.Append(piece);
            return sb.ToString();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
