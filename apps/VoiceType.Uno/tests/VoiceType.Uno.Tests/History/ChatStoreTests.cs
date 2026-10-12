using System.Text.Json;
using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class ChatStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(HistoryFixtures.T0);
    private readonly ChatStore _store;

    public ChatStoreTests() => _store = new ChatStore(Path.Combine(_directory.Path, "chats"), _clock);

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Create_ShouldWriteChatJsonWithAllSources()
    {
        var chat = _store.Create("Sync Q&A", [("aaaaaa", "v1"), ("bbbbbb", "v2"), ("aaaaaa", "v3")]);

        Assert.True(File.Exists(Path.Combine(_store.ChatsDir, chat.Id, ChatStore.ChatFileName)));
        Assert.Equal(["aaaaaa", "bbbbbb"], chat.Sources.Select(s => s.RecordingId));
        Assert.Equal(HistoryFixtures.T0, chat.CreatedAt);
        Assert.Equal("Sync Q&A", _store.Get(chat.Id)!.Title);
    }

    [Fact]
    public void Create_ShouldRequireAtLeastOneSourceAndDefaultTheTitle()
    {
        Assert.Throws<ArgumentException>(() => _store.Create("x", Array.Empty<(string, string)>()));
        Assert.Equal("Chat 2026-10-12 14:02", _store.Create(null, "aaaaaa", "v1").Title);
    }

    [Fact]
    public void AddSource_ShouldAddNewRecordingOrMoveVersion()
    {
        var chat = _store.Create("c", "aaaaaa", "v1");
        _clock.Advance(TimeSpan.FromMinutes(5));

        _store.AddSource(chat.Id, "bbbbbb", "v1");
        var updated = _store.AddSource(chat.Id, "aaaaaa", "v2");

        Assert.Equal(2, updated.Sources.Count);
        Assert.Equal("v2", updated.Sources[0].VersionId);
        Assert.Equal(HistoryFixtures.T0.AddMinutes(5), updated.Sources[1].AddedAt);
        Assert.Equal(HistoryFixtures.T0.AddMinutes(5), updated.UpdatedAt);
    }

    [Fact]
    public void RemoveSource_ShouldRemoveButNeverTheLastOne()
    {
        var chat = _store.Create("c", [("aaaaaa", "v1"), ("bbbbbb", "v1")]);

        Assert.Single(_store.RemoveSource(chat.Id, "aaaaaa").Sources);
        Assert.Throws<InvalidOperationException>(() => _store.RemoveSource(chat.Id, "bbbbbb"));
    }

    [Fact]
    public void AppendMessage_ShouldAssignIdsAndTimestamps()
    {
        var chat = _store.Create("c", "aaaaaa", "v1");

        _store.AppendMessage(chat.Id, new ChatMessage { Role = ChatRoles.User, Text = "When?" });
        var updated = _store.AppendMessage(chat.Id, new ChatMessage
        {
            Role = ChatRoles.Assistant, Text = "Monday.",
            Citations = [new ChatCitation { RecordingId = "aaaaaa", StartSec = 200 }],
        });

        Assert.Equal(["m1", "m2"], updated.Messages.Select(m => m.Id));
        Assert.All(updated.Messages, m => Assert.Equal(HistoryFixtures.T0, m.CreatedAt));
        Assert.Equal(200, _store.Get(chat.Id)!.Messages[1].Citations[0].StartSec);
    }

    [Fact]
    public void SaveAgentSession_ShouldPersistOpaqueBlob()
    {
        var chat = _store.Create("c", "aaaaaa", "v1");
        using var blob = JsonDocument.Parse("""{"stateBag":{"k":[1,2,3]}}""");

        _store.SaveAgentSession(chat.Id, blob.RootElement);

        var loaded = new ChatStore(_store.ChatsDir).Get(chat.Id)!;
        Assert.Equal(3, loaded.AgentSession!.Value.GetProperty("stateBag").GetProperty("k").GetArrayLength());
        Assert.Null(_store.SaveAgentSession(chat.Id, null).AgentSession);
    }

    [Fact]
    public void RenameAndDelete_ShouldWork()
    {
        var chat = _store.Create("c", "aaaaaa", "v1");

        Assert.Equal("Renamed", _store.Rename(chat.Id, " Renamed ").Title);
        _store.Delete(chat.Id);

        Assert.Null(_store.Get(chat.Id));
        Assert.Throws<KeyNotFoundException>(() => _store.Rename(chat.Id, "x"));
    }

    [Fact]
    public void ListIncluding_ShouldReturnChatsOfARecordingNewestFirst()
    {
        var first = _store.Create("first", "aaaaaa", "v1");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = _store.Create("second", [("bbbbbb", "v1"), ("aaaaaa", "v2")]);
        _store.Create("other", "cccccc", "v1");

        Assert.Equal([second.Id, first.Id], _store.ListIncluding("aaaaaa").Select(c => c.Id));
        Assert.Equal([first.Id], _store.ListReferencing("aaaaaa", "v1").Select(c => c.Id));
    }

    [Fact]
    public void LoadAll_ShouldReportCorruptedChats()
    {
        _store.Create("ok", "aaaaaa", "v1");
        _directory.Write(Path.Combine("chats", "cbroken", "chat.json"), "{");

        var result = _store.LoadAll();

        Assert.Single(result.Chats);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void GetOutdatedSources_ShouldFlagMovedAndMissingRecordings()
    {
        var chat = _store.Create("c", [("aaaaaa", "v1"), ("bbbbbb", "v2"), ("cccccc", "v1")]);
        var current = new Dictionary<string, string> { ["aaaaaa"] = "v3", ["bbbbbb"] = "v2" };

        var outdated = ChatStore.GetOutdatedSources(chat, id => current.GetValueOrDefault(id));

        Assert.Equal(2, outdated.Count);
        Assert.Equal(new OutdatedSource("aaaaaa", "v1", "v3"), outdated[0]);
        Assert.True(outdated[1].RecordingMissing);
    }

    [Fact]
    public void RemoveRecordingFromAll_ShouldDropSourcesAndOptionallyEmptyChats()
    {
        var shared = _store.Create("shared", [("aaaaaa", "v1"), ("bbbbbb", "v1")]);
        var only = _store.Create("only", "aaaaaa", "v1");

        var affected = _store.RemoveRecordingFromAll("aaaaaa", deleteEmptyChats: true);

        Assert.Equal(2, affected.Count);
        Assert.Equal(["bbbbbb"], _store.Get(shared.Id)!.Sources.Select(s => s.RecordingId));
        Assert.Null(_store.Get(only.Id));
    }

    [Fact]
    public void AppendMessage_ShouldKeepEveryConcurrentMessage()
    {
        var chat = _store.Create("c", "aaaaaa", "v1");

        Parallel.For(0, 25, i => _store.AppendMessage(chat.Id, new ChatMessage { Text = "msg " + i }));

        Assert.Equal(25, _store.Get(chat.Id)!.Messages.Count);
    }
}
