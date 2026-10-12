using SpeechLib.Agents;
using SpeechLib.Llm;
using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>One pipeline, one cache: the summarize job and chat share <c>analysis/vN.chunks.json</c> + <c>vN.summary.json</c>.</summary>
public class SharedSummaryCacheTests : IDisposable
{
    private const string Map = "You summarize";
    private const string Reduce = "You combine";

    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(HistoryFixtures.T0);
    private readonly RecordingStore _recordings;
    private readonly ChatStore _chats;
    private readonly FakeLlmHost _host = new();
    private readonly AnalysisStore _analysis;

    public SharedSummaryCacheTests()
    {
        _recordings = new RecordingStore(Path.Combine(_directory.Path, "sessions"), _clock);
        _chats = new ChatStore(Path.Combine(_directory.Path, "chats"));
        _analysis = new AnalysisStore(_recordings);
        _host.Responder = (r, _) =>
        {
            var system = r.Messages[0].Text;
            if (system.StartsWith(Map, StringComparison.Ordinal))
                return "Chunk summary [00:05].";
            if (system.StartsWith(Reduce, StringComparison.Ordinal))
                return "Whole summary [00:05].";
            return "The answer [S1 00:05].";
        };
    }

    public void Dispose() => _directory.Dispose();

    private ChatService NewChat(JobQueue? jobs = null) => new(_recordings, _chats, _host, jobs: jobs);

    private string AddRecording(int sentences = 150, double durationSec = 0)
    {
        var entry = _recordings.CreateRecording(HistoryFixtures.T0);
        var segments = Enumerable.Range(0, sentences)
            .Select(i => ((double)i * 5, (double)i * 5 + 4, $"This is sentence number {i} about the quarterly budget topic{i}."))
            .ToArray();
        _recordings.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript(segments));
        _recordings.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(1));
        if (durationSec > 0)
            _recordings.Update(entry.Id, m => m.Audio = new AudioInfo { File = "audio.ogg", DurationSec = durationSec });
        return entry.Id;
    }

    private static HistoryJob Job(string recordingId) => new() { Id = "j", Kind = HistoryJobKind.Summarize, RecordingId = recordingId };

    private int Count(string systemPrefix) =>
        _host.Requests.Count(r => r.Messages[0].Text.StartsWith(systemPrefix, StringComparison.Ordinal));

    private static async Task<List<ChatEvent>> CollectAsync(IAsyncEnumerable<ChatEvent> stream)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in stream)
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task ChatSummary_ShouldReuseTheChunksAndSummaryTheJobWroteWithoutAnyMapCall()
    {
        var id = AddRecording();
        await new SummarizeJobWorker(_recordings, _host, _clock).RunAsync(Job(id), NullJobProgressSink.Instance, CancellationToken.None);
        var chunks = _analysis.LoadChunks(id, "v1")!;
        var mapCalls = Count(Map);
        var reduceCalls = Count(Reduce);
        Assert.Equal(chunks.Chunks.Count, mapCalls);
        var before = _host.Requests.Count;

        var service = NewChat();
        var chat = service.CreateChat([id]);
        var events = await CollectAsync(service.SendAsync(chat.Id, "Summarize this recording"));

        Assert.IsType<ChatEvent.Completed>(events[^1]);
        Assert.Equal(1, _host.Requests.Count - before); // only the answer
        Assert.Equal(mapCalls, Count(Map));
        Assert.Equal(reduceCalls, Count(Reduce));
        Assert.DoesNotContain(events, e => e is ChatEvent.Status s && s.Text.StartsWith("Summarising", StringComparison.Ordinal));
        // The digest the model saw is made of the job's text, with citable aliases.
        Assert.Contains("[S1 00:05]", _host.Requests[^1].Messages.Last(m => m.Text.Contains("Summary of S1")).Text);
    }

    [Fact]
    public async Task ChatSummary_ShouldWriteChunksAsTheJobFormatAndNoLegacyFile()
    {
        var id = AddRecording();
        var service = NewChat();
        var chat = service.CreateChat([id]);

        var events = await CollectAsync(service.SendAsync(chat.Id, "Summarize this recording"));

        Assert.Contains(events, e => e is ChatEvent.Status s && s.Text.StartsWith("Summarising part 1 of ", StringComparison.Ordinal));
        var chunks = _analysis.LoadChunks(id, "v1")!;
        Assert.True(chunks.Complete);
        Assert.Equal(SummaryDefaults.ChunkTokens, chunks.ChunkTokens);
        Assert.All(chunks.Chunks, c => Assert.Equal("Chunk summary [00:05].", c.Summary));
        var folder = _recordings.TryGetFolder(id)!;
        Assert.False(File.Exists(Path.Combine(folder, AnalysisStore.LegacyChatSummariesFile("v1"))));
        // The map units are Interactive in chat.
        Assert.All(_host.Requests.Where(r => r.Messages[0].Text.StartsWith(Map, StringComparison.Ordinal)), r => Assert.Equal(LlmPriority.Interactive, r.Priority));
        var calls = _host.Requests.Count;

        // The digest fit the chat budget, so no reduce was needed; the job finds every chunk done and only reduces.
        var mapCalls = Count(Map);
        await new SummarizeJobWorker(_recordings, _host, _clock).RunAsync(Job(id), NullJobProgressSink.Instance, CancellationToken.None);
        Assert.Equal(mapCalls, Count(Map));
        Assert.Equal(calls + 1, _host.Requests.Count);
        Assert.Equal("Whole summary [00:05].", _analysis.LoadSummary(id, "v1")!.Text);
    }

    [Fact]
    public async Task ChatSummary_ShouldContinueAJobThatWasCancelledHalfWay()
    {
        var id = AddRecording();
        using var cts = new CancellationTokenSource();
        _host.Before = (_, call, _) =>
        {
            if (call == 3)
                cts.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SummarizeJobWorker(_recordings, _host, _clock).RunAsync(Job(id), NullJobProgressSink.Instance, cts.Token));
        _host.Before = null;
        var chunks = _analysis.LoadChunks(id, "v1")!;
        var done = chunks.Chunks.Count(c => c.Summary is not null);
        Assert.Equal(2, done);
        var mapBefore = Count(Map);

        var service = NewChat();
        var chat = service.CreateChat([id]);
        var events = await CollectAsync(service.SendAsync(chat.Id, "Give me an overview"));

        Assert.IsType<ChatEvent.Completed>(events[^1]);
        Assert.Equal(chunks.Chunks.Count - done, Count(Map) - mapBefore);
        Assert.Contains(events, e => e is ChatEvent.Status s && s.Text == $"Summarising part 3 of {chunks.Chunks.Count}…");
        Assert.True(_analysis.LoadChunks(id, "v1")!.Complete);
    }

    [Fact]
    public async Task ChatSummary_ShouldRebuildWhenTheStoredHashDoesNotMatchTheText()
    {
        var id = AddRecording();
        await new SummarizeJobWorker(_recordings, _host, _clock).RunAsync(Job(id), NullJobProgressSink.Instance, CancellationToken.None);
        var stale = _analysis.LoadChunks(id, "v1")!;
        stale.TextHash = "0123456789abcdef0123456789abcdef";
        _analysis.SaveChunks(id, stale);
        var staleSummary = _analysis.LoadSummary(id, "v1")!;
        staleSummary.TextHash = stale.TextHash;
        _analysis.SaveSummary(id, staleSummary);
        var mapBefore = Count(Map);

        var service = NewChat();
        var chat = service.CreateChat([id]);
        await CollectAsync(service.SendAsync(chat.Id, "Summarize this recording"));

        Assert.Equal(stale.Chunks.Count, Count(Map) - mapBefore);
        var fresh = _analysis.LoadChunks(id, "v1")!;
        Assert.NotEqual("0123456789abcdef0123456789abcdef", fresh.TextHash);
        Assert.True(fresh.Complete);
    }

    [Fact]
    public async Task OldChatSummariesFile_ShouldBeIgnoredAndRemovedOnceTheSharedCacheExists()
    {
        var id = AddRecording();
        var folder = _recordings.TryGetFolder(id)!;
        Directory.CreateDirectory(Path.Combine(folder, "analysis"));
        var legacy = Path.Combine(folder, AnalysisStore.LegacyChatSummariesFile("v1"));
        File.WriteAllText(legacy, "{\"s2:c:0123456789abcdef\":\"Old summary that must never be shown.\"}");
        var service = NewChat();
        var chat = service.CreateChat([id]);

        await CollectAsync(service.SendAsync(chat.Id, "Summarize this recording"));

        Assert.False(File.Exists(legacy));
        Assert.DoesNotContain(_host.Requests.SelectMany(r => r.Messages), m => m.Text.Contains("Old summary that must never be shown"));
        Assert.True(Count(Map) > 0); // the old file was not read: everything was computed into the shared cache
    }

    [Fact]
    public void DeleteVersion_ShouldAlsoDropTheOldChatSummariesFile()
    {
        var id = AddRecording(5);
        _recordings.SaveEdit(id, "v1", "Edited.");
        var folder = _recordings.TryGetFolder(id)!;
        Directory.CreateDirectory(Path.Combine(folder, "analysis"));
        var legacy = Path.Combine(folder, AnalysisStore.LegacyChatSummariesFile("v1"));
        File.WriteAllText(legacy, "{}");

        _recordings.DeleteVersion(id, "v1");

        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void Store_ShouldRoundTripChunksAndSummaryKeyedByVersionIndexAndHash()
    {
        var id = AddRecording(150);
        var store = new AnalysisSummaryStore(_recordings, _clock);
        var target = new SummaryTarget(id, "v1", () => []);

        var chunks = store.OpenChunks(target, _host.CountTokens);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.Null(c.Summary));
        store.SaveChunkSummary(target, 1, "Second part [01:00].");
        store.SaveSummary(target, "Whole.", chunks.Count);

        var other = new AnalysisSummaryStore(_recordings, _clock); // a second instance (the job in another scope) sees it
        var reopened = other.OpenChunks(target, _host.CountTokens);
        Assert.Null(reopened[0].Summary);
        Assert.Equal("Second part [01:00].", reopened[1].Summary);
        Assert.Equal("Second part [01:00].", other.GetChunkSummary(target, 1));
        Assert.Equal("Whole.", other.LoadSummary(target));

        var document = _analysis.LoadChunks(id, "v1")!;
        Assert.Equal(1, document.Schema);
        Assert.Equal(SummaryDefaults.PromptVersion, document.PromptVersion);
        Assert.Equal("gemma-4-E2B-it", document.Model);
        Assert.Equal(1, _analysis.LoadSummary(id, "v1")!.Schema);

        // A summary of the wrong chunk count is not saved; an edited text starts empty.
        store.SaveSummary(target, "Wrong.", chunks.Count + 1);
        Assert.Equal("Whole.", store.LoadSummary(target));
        _recordings.SaveEdit(id, "v1", "Completely different text.");
        var edited = new SummaryTarget(id, "v2", () => []);
        Assert.All(store.OpenChunks(edited, _host.CountTokens), c => Assert.Null(c.Summary));
        Assert.Null(store.LoadSummary(edited));
    }

    // ── background precompute and chat preparation ───────────────────────

    private JobQueue OpenQueue() => new(Path.Combine(_directory.Path, "jobs", "queue.json"), _clock);

    [Fact]
    public void AfterSave_ShouldQueueABackgroundSummaryOnlyForLongRecordingsWithTheModelAndTheToggle()
    {
        var longId = AddRecording(150, durationSec: 900);
        var shortId = AddRecording(20, durationSec: 120);
        var queue = OpenQueue();
        var on = new HistorySettings { AutoTitles = true };

        HistoryLlmJobs.TryEnqueueSummarizeAfterSave(queue, _recordings, shortId, on, modelInstalled: true);
        HistoryLlmJobs.TryEnqueueSummarizeAfterSave(queue, _recordings, longId, new HistorySettings { AutoTitles = false }, modelInstalled: true);
        HistoryLlmJobs.TryEnqueueSummarizeAfterSave(queue, _recordings, longId, on, modelInstalled: false);
        Assert.Empty(queue.Jobs);

        HistoryLlmJobs.TryEnqueueSummarizeAfterSave(queue, _recordings, longId, on, modelInstalled: true);
        HistoryLlmJobs.TryEnqueueSummarizeAfterSave(queue, _recordings, longId, on, modelInstalled: true);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(HistoryJobKind.Summarize, job.Kind);
        Assert.Equal(JobPriorities.Low, job.Priority);
        Assert.Equal("v1", job.Parameters.SourceVersion);
    }

    [Fact]
    public async Task Chat_ShouldQueueThePreparationAtHighPriorityAndShowItsProgress()
    {
        var id = AddRecording(150, durationSec: 900);
        var queue = OpenQueue();
        // The after-save job is still waiting at low priority.
        var low = HistoryLlmJobs.EnqueueSummarize(queue, id, "v1", JobPriorities.Low);
        var service = NewChat(queue);

        var chat = service.CreateChat([id]);

        var job = Assert.Single(queue.Jobs.Where(j => !j.IsFinished));
        Assert.Equal(JobPriorities.High, job.Priority);
        Assert.NotEqual(low.Id, job.Id);
        Assert.Equal("Preparing the recording…", service.PreparationStatus(chat.Id));

        // Part of it was done by the job: the status counts chunks (+ the reduce step).
        var store = new AnalysisSummaryStore(_recordings, _clock);
        var target = new SummaryTarget(id, "v1", () => []);
        var chunks = store.OpenChunks(target, _host.CountTokens);
        store.SaveChunkSummary(target, 0, "One.");
        store.SaveChunkSummary(target, 1, "Two.");
        Assert.Equal($"Preparing the recording… 2/{chunks.Count + 1}", service.PreparationStatus(chat.Id));

        var events = await CollectAsync(service.SendAsync(chat.Id, "Summarize this recording"));
        Assert.Contains(events, e => e is ChatEvent.Status s && s.Text.StartsWith("Preparing the recording…", StringComparison.Ordinal));
    }

    [Fact]
    public void Chat_ShouldNotQueueAnythingForShortRecordingsOrACompleteSummary()
    {
        var shortId = AddRecording(20, durationSec: 120);
        var doneId = AddRecording(150, durationSec: 900);
        var queue = OpenQueue();
        var store = new AnalysisSummaryStore(_recordings, _clock);
        var target = new SummaryTarget(doneId, "v1", () => []);
        var count = store.OpenChunks(target, _host.CountTokens).Count;
        for (var i = 0; i < count; i++)
            store.SaveChunkSummary(target, i, "x");
        store.SaveSummary(target, "Whole.", count);
        var service = NewChat(queue);

        service.CreateChat([shortId, doneId]);

        Assert.Empty(queue.Jobs);
    }
}
