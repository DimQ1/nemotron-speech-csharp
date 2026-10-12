using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using SpeechLib.Agents;
using SpeechLib.Llm;

namespace VoiceType.Uno.History;

/// <summary>
/// <see cref="IChatService"/> over <see cref="RecordingStore"/>, <see cref="ChatStore"/> and the on-device Gemma
/// (<see cref="ILlmHost"/>) through the agent layer (<see cref="TranscriptChatAgent"/>).
/// </summary>
/// <remarks>
/// The display log (<see cref="ChatRecord.Messages"/>) is the source of truth for a conversation; the serialized
/// Agent Framework session (<see cref="ChatRecord.AgentSession"/>) is an accelerator. When the stored session
/// cannot be read (a library format change) or no longer matches the log, it is rebuilt from the log, so no
/// conversation is ever lost. Aliases of the sources live in the log as system events
/// (<see cref="ChatSourceEvents"/>).
/// </remarks>
public sealed class ChatService : IChatService
{
    private readonly RecordingStore _recordings;
    private readonly ChatStore _chats;
    private readonly ILlmHost _host;
    private readonly ISummaryStore _summaries;
    private readonly JobQueue? _jobs;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChatRuntime> _runtimes = new(StringComparer.Ordinal);

    public ChatService(RecordingStore recordings, ChatStore chats, ILlmHost host, ISummaryStore? summaries = null, TimeProvider? timeProvider = null, JobQueue? jobs = null)
    {
        _recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
        _chats = chats ?? throw new ArgumentNullException(nameof(chats));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _summaries = summaries ?? new AnalysisSummaryStore(recordings, timeProvider);
        _jobs = jobs;
        _time = timeProvider ?? TimeProvider.System;
        QuickPrompts = ChatPrompts.QuickPrompts.Select(p => new QuickPrompt(p.Id, p.Label, p.Prompt)).ToList();
    }

    public bool IsAvailable => _host.IsModelInstalled;

    public IReadOnlyList<QuickPrompt> QuickPrompts { get; }

    // ---- chats and sources ----

    public ChatRecord CreateChat(IEnumerable<string> recordingIds, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(recordingIds);
        var sources = new List<(string RecordingId, string VersionId)>();
        foreach (var id in recordingIds.Distinct(StringComparer.Ordinal))
        {
            var version = _recordings.Get(id)?.Manifest.CurrentVersion;
            if (version is not null)
                sources.Add((id, version));
        }
        if (sources.Count == 0)
            throw new ArgumentException("None of the recordings has a transcript to chat about.", nameof(recordingIds));

        var chat = _chats.Create(title, sources);
        foreach (var (recordingId, versionId) in sources)
            chat = LogSourceEvent(chat.Id, ChatSourceEvents.AddedKind, recordingId, versionId) ?? chat;
        foreach (var (recordingId, versionId) in sources)
            PrepareSummaries(recordingId, versionId);
        return chat;
    }

    public ChatRecord AddSource(string chatId, string recordingId)
    {
        var version = _recordings.Get(recordingId)?.Manifest.CurrentVersion
            ?? throw new InvalidOperationException("That recording has no transcript to chat about.");
        var before = _chats.Get(chatId) ?? throw new KeyNotFoundException($"Chat {chatId} not found.");
        EnsureAliasEvents(before);
        if (before.Includes(recordingId))
            return before;

        _chats.AddSource(chatId, recordingId, version);
        PrepareSummaries(recordingId, version);
        return LogSourceEvent(chatId, ChatSourceEvents.AddedKind, recordingId, version) ?? _chats.Get(chatId)!;
    }

    public ChatRecord RemoveSource(string chatId, string recordingId)
    {
        var before = _chats.Get(chatId) ?? throw new KeyNotFoundException($"Chat {chatId} not found.");
        EnsureAliasEvents(before);
        var source = before.Sources.Find(s => s.RecordingId == recordingId);
        if (source is null)
            return before;

        _chats.RemoveSource(chatId, recordingId); // throws when it is the last one
        return LogSourceEvent(chatId, ChatSourceEvents.RemovedKind, recordingId, source.VersionId) ?? _chats.Get(chatId)!;
    }

    /// <summary>Moves an outdated source to the recording's current transcript version ("use v3").</summary>
    public ChatRecord UseCurrentVersion(string chatId, string recordingId)
    {
        var version = _recordings.Get(recordingId)?.Manifest.CurrentVersion
            ?? throw new InvalidOperationException("That recording has no transcript.");
        var chat = _chats.Get(chatId) ?? throw new KeyNotFoundException($"Chat {chatId} not found.");
        EnsureAliasEvents(chat);
        if (chat.Sources.Find(s => s.RecordingId == recordingId)?.VersionId == version)
            return chat;
        _chats.AddSource(chatId, recordingId, version); // moves the existing source
        PrepareSummaries(recordingId, version);
        return LogSourceEvent(chatId, ChatSourceEvents.VersionChangedKind, recordingId, version) ?? _chats.Get(chatId)!;
    }

    /// <summary>Sources whose recording moved on to another transcript version (or was deleted).</summary>
    public IReadOnlyList<OutdatedSource> GetOutdatedSources(string chatId)
    {
        var chat = _chats.Get(chatId) ?? throw new KeyNotFoundException($"Chat {chatId} not found.");
        return ChatStore.GetOutdatedSources(chat, id => _recordings.Get(id)?.Manifest.CurrentVersion);
    }

    /// <summary>The alias (S1…) of every source of the chat, for chips and citation labels.</summary>
    public IReadOnlyDictionary<string, string> GetAliases(string chatId)
    {
        var chat = _chats.Get(chatId) ?? throw new KeyNotFoundException($"Chat {chatId} not found.");
        var registry = ChatSourceEvents.BuildRegistry(chat, out _);
        return registry.ActiveEntries.ToDictionary(e => e.RecordingId, e => e.Alias, StringComparer.Ordinal);
    }

    public void CloseChat(string chatId)
    {
        ChatRuntime? runtime;
        lock (_gate)
        {
            if (!_runtimes.Remove(chatId, out runtime))
            {
                _host.ReleaseConversation(chatId);
                return;
            }
        }
        runtime.Agent.Dispose();
    }

    // ---- sending ----

    public async IAsyncEnumerable<ChatEvent> SendAsync(string chatId, string userText, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ChatEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() => ProduceAsync(chatId, userText, channel.Writer, linked.Token), CancellationToken.None);
        try
        {
            await foreach (var chatEvent in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                yield return chatEvent;
        }
        finally
        {
            // The consumer left early: stop the engine; the producer saves what was written so far.
            linked.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(string chatId, string userText, ChannelWriter<ChatEvent> output, CancellationToken ct)
    {
        try
        {
            await RunTurnAsync(chatId, userText, output, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            output.TryWrite(new ChatEvent.Failed(ex.Message));
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task RunTurnAsync(string chatId, string userText, ChannelWriter<ChatEvent> output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            output.TryWrite(new ChatEvent.Failed("Type a question first."));
            return;
        }
        var chat = _chats.Get(chatId);
        if (chat is null)
        {
            output.TryWrite(new ChatEvent.Failed("This chat no longer exists."));
            return;
        }
        if (!_host.IsModelInstalled)
        {
            output.TryWrite(new ChatEvent.Failed("The Gemma model is not installed. Download it in Models to ask questions about recordings."));
            return;
        }

        var runtime = GetRuntime(chat);
        if (!runtime.Turn.Wait(0, CancellationToken.None))
        {
            output.TryWrite(new ChatEvent.Failed("The previous answer is still being written."));
            return;
        }

        var answer = new StringBuilder();
        var failure = (string?)null;
        var cancelled = false;
        void OnHostStatus(LlmHostStatus s)
        {
            if (answer.Length == 0 && !string.IsNullOrWhiteSpace(s.Detail) && s.State == LlmHostState.Running)
                output.TryWrite(new ChatEvent.Status(s.Detail!));
        }

        try
        {
            EnsureAliasEvents(chat);
            chat = _chats.Get(chatId) ?? chat;
            var before = chat.Messages;   // the history the session must match: without the new question

            _chats.AppendMessage(chatId, new ChatMessage { Role = ChatRoles.User, Text = userText.Trim(), CreatedAt = _time.GetLocalNow() });

            var count = chat.Sources.Count;
            output.TryWrite(new ChatEvent.Status(count == 1 ? "Reading the recording…" : $"Reading {count} recordings…"));
            if (PreparationStatus(chat) is { } preparing)
                output.TryWrite(new ChatEvent.Status(preparing));

            var session = await EnsureSessionAsync(runtime, chat, before, ct).ConfigureAwait(false);
            _host.StatusChanged += OnHostStatus;
            try
            {
                await foreach (var piece in runtime.Agent.RunStreamingAsync(session, userText.Trim(), new SyncProgress(text => output.TryWrite(new ChatEvent.Status(text))), ct).ConfigureAwait(false))
                {
                    answer.Append(piece);
                    output.TryWrite(new ChatEvent.Delta(piece));
                }
            }
            finally
            {
                _host.StatusChanged -= OnHostStatus;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex is LlmUnavailableException ? "The Gemma model is not available: " + ex.Message : ex.Message;
        }

        try
        {
            if (cancelled || failure is not null)
            {
                // Whatever was written is kept, marked incomplete; the session is rebuilt from the log next turn.
                runtime.Session = null;
                var partial = answer.ToString().Trim();
                if (partial.Length > 0)
                {
                    var saved = SaveAnswer(chatId, partial, runtime.Catalog, incomplete: true);
                    if (failure is not null)
                        output.TryWrite(new ChatEvent.Failed(failure));
                    output.TryWrite(new ChatEvent.Completed(saved));
                }
                else if (failure is not null)
                {
                    output.TryWrite(new ChatEvent.Failed(failure));
                }
                return;
            }

            var finalText = answer.ToString().Trim();
            if (finalText.Length == 0)
            {
                runtime.Session = null;
                output.TryWrite(new ChatEvent.Failed("Gemma returned an empty answer. Try asking again."));
                return;
            }

            var message = SaveAnswer(chatId, finalText, runtime.Catalog, incomplete: false);
            try
            {
                runtime.Agent.ReplaceLastAnswer(runtime.Session!, message.Text);
                _chats.SaveAgentSession(chatId, await runtime.Agent.SerializeAsync(runtime.Session!, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The log has the answer; the session is rebuilt from it on the next turn.
                runtime.Session = null;
            }
            output.TryWrite(new ChatEvent.Completed(message));
        }
        finally
        {
            runtime.Turn.Release();
        }
    }

    private ChatMessage SaveAnswer(string chatId, string rawText, IChatSourceCatalog catalog, bool incomplete)
    {
        var parsed = CitationParser.Parse(rawText, catalog.GetSources());
        var message = new ChatMessage
        {
            Role = ChatRoles.Assistant,
            Text = parsed.Text,
            CreatedAt = _time.GetLocalNow(),
            Incomplete = incomplete,
            Citations = parsed.Citations.Select(c => new ChatCitation
            {
                RecordingId = c.RecordingId,
                VersionId = c.VersionId,
                StartSec = c.StartSec,
                EndSec = c.EndSec,
                Quote = c.Quote,
                Speaker = c.Speaker,
            }).ToList(),
        };
        var record = _chats.AppendMessage(chatId, message);
        return record.Messages[^1];
    }

    // ---- runtime (agent + session per open chat) ----

    private ChatRuntime GetRuntime(ChatRecord chat)
    {
        lock (_gate)
        {
            if (_runtimes.TryGetValue(chat.Id, out var existing))
                return existing;
            var catalog = new ChatSourceCatalog(chat.Id, _chats, _recordings);
            var agent = new TranscriptChatAgent(_host, catalog, chat.Id, _summaries);
            return _runtimes[chat.Id] = new ChatRuntime(agent, catalog);
        }
    }

    private async Task<TranscriptChatSession> EnsureSessionAsync(ChatRuntime runtime, ChatRecord chat, IReadOnlyList<ChatMessage> logBefore, CancellationToken ct)
    {
        var turns = ToTurns(logBefore);
        if (runtime.Session is { } live && runtime.Agent.GetHistory(live).Count == turns.Count)
            return live;

        TranscriptChatSession? session = null;
        if (chat.AgentSession is { } stored)
        {
            session = await runtime.Agent.TryDeserializeAsync(stored, ct).ConfigureAwait(false);
            // A stored session that disagrees with the log (a crash between the two writes) is not trusted.
            if (session is not null && runtime.Agent.GetHistory(session).Count != turns.Count)
                session = null;
        }
        session ??= await runtime.Agent.RebuildSessionAsync(turns, ct).ConfigureAwait(false);
        runtime.Session = session;
        return session;
    }

    /// <summary>
    /// The model-facing turns of the display log: complete question→answer pairs only (an unanswered or
    /// failed question and source-change events are not sent to the model).
    /// </summary>
    internal static List<ChatTurn> ToTurns(IEnumerable<ChatMessage> messages)
    {
        var turns = new List<ChatTurn>();
        string? pendingUser = null;
        foreach (var m in messages)
        {
            if (m.Role == ChatRoles.User)
                pendingUser = m.Text;
            else if (m.Role == ChatRoles.Assistant && pendingUser is not null && !string.IsNullOrWhiteSpace(m.Text))
            {
                turns.Add(new ChatTurn(true, pendingUser));
                turns.Add(new ChatTurn(false, m.Text));
                pendingUser = null;
            }
        }
        return turns;
    }

    // ---- shared summaries (one pipeline with the summarize job) ----

    /// <summary>
    /// Makes sure a long recording's summaries are being prepared: queues the Background <c>summarize</c> job at the
    /// highest Background priority when the whole-recording summary is missing. The job and chat share one cache
    /// (<see cref="AnalysisSummaryStore"/>), so a first "summarize" question finds the chunks already done.
    /// Never throws: without it the first question computes the missing chunks itself.
    /// </summary>
    private void PrepareSummaries(string recordingId, string versionId)
    {
        if (_jobs is null || _jobs.IsReadOnly || !_host.IsModelInstalled)
            return;
        try
        {
            var manifest = _recordings.Get(recordingId)?.Manifest;
            if (manifest is null || manifest.Audio?.DurationSec is not > HistoryLlmJobs.PrepareChatMinSeconds)
                return;
            if (_summaries.LoadSummary(new SummaryTarget(recordingId, versionId, () => [])) is not null)
                return;
            HistoryLlmJobs.EnqueueSummarize(_jobs, recordingId, versionId, JobPriorities.High);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.WriteLine($"[VoiceType.Uno] Summary job was not queued: {ex.Message}");
        }
    }

    /// <summary>"Preparing the recording… 3/12" while a summarize job of one of the chat's sources is waiting or running.</summary>
    public string? PreparationStatus(string chatId) =>
        _chats.Get(chatId) is { } chat ? PreparationStatus(chat) : null;

    private string? PreparationStatus(ChatRecord chat)
    {
        if (_jobs is null)
            return null;
        foreach (var source in chat.Sources)
        {
            if (HistoryLlmJobs.PreparationText(_jobs, _recordings, source.RecordingId, source.VersionId) is { } text)
                return text;
        }
        return null;
    }

    // ---- aliases ----

    private void EnsureAliasEvents(ChatRecord chat)
    {
        ChatSourceEvents.BuildRegistry(chat, out var unlogged);
        foreach (var recordingId in unlogged)
        {
            var source = chat.Sources.Find(s => s.RecordingId == recordingId);
            if (source is not null)
                LogSourceEvent(chat.Id, ChatSourceEvents.AddedKind, recordingId, source.VersionId);
        }
    }

    /// <summary>
    /// Appends one source event to the log, with the alias the registry has for that recording. Unlogged older
    /// sources are logged first (<see cref="EnsureAliasEvents"/>), so aliases always follow the source order.
    /// </summary>
    private ChatRecord? LogSourceEvent(string chatId, string kind, string recordingId, string versionId)
    {
        var chat = _chats.Get(chatId);
        if (chat is null)
            return null;
        var alias = ChatSourceEvents.BuildRegistry(chat, out _).AliasOf(recordingId);
        if (alias is null)
            return chat;
        return _chats.AppendMessage(chatId, ChatSourceEvents.Create(kind, alias, recordingId, versionId, TitleOf(recordingId), _time.GetLocalNow()));
    }

    private string TitleOf(string recordingId) => _recordings.Get(recordingId)?.Manifest.EffectiveTitle ?? recordingId;

    private sealed class ChatRuntime(TranscriptChatAgent agent, IChatSourceCatalog catalog)
    {
        public TranscriptChatAgent Agent { get; } = agent;
        public IChatSourceCatalog Catalog { get; } = catalog;
        public TranscriptChatSession? Session { get; set; }
        public SemaphoreSlim Turn { get; } = new(1, 1);
    }

    /// <summary>Progress that runs inline (the framework's Progress{T} posts to a sync context and may reorder).</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
