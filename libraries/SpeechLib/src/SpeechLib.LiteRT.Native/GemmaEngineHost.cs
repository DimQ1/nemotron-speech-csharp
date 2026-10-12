using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native;

/// <summary>
/// The single owner of the process's one Gemma engine (LiteRT-LM through LiteRtLmSharp 1.3.0): loads it lazily,
/// unloads it when idle, and runs every native operation through ONE worker loop, so exactly one operation is ever
/// in flight (the "lease": the runtime serializes sends without priorities and blocks conversation creation and
/// token counting while anything generates).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Queue: Live, then Interactive, then Background; FIFO inside a class.</item>
/// <item>A higher class preempts a running lower one by cancelling it (about 40 ms during decode; a prefill cannot be
/// cancelled, so the host reports the wait in <see cref="Status"/>).</item>
/// <item>A preempted Background request is re-run from its start (its text is delivered whole, never half-streamed).
/// A preempted Interactive request keeps its enumerator open, resumes on a fresh conversation from the text already
/// yielded (<c>Snapshot()/Resume()</c> plus a continuation turn) and goes on without duplicates.</item>
/// <item>Requests with a <see cref="LlmRequest.ConversationKey"/> use a live stateful conversation (LRU, at most
/// <see cref="GemmaEngineHostOptions.MaxLiveConversations"/>) when the caller's history matches what the conversation
/// holds, and rebuild it by replay otherwise.</item>
/// </list>
/// </remarks>
public sealed class GemmaEngineHost : ILlmHost
{
    private const int MaxPreemptionsPerRequest = 200;

    private readonly GemmaEngineHostOptions _options;
    private readonly Func<IGemmaEngine> _engineFactory;
    private readonly Func<bool> _isInstalled;

    private readonly object _lock = new();
    private readonly List<Job> _queue = [];
    private Job? _running;
    private long _seq;
    private bool _disposed;
    private string? _unavailableReason;
    private string? _detail;
    private LlmHostStatus? _lastPublished;

    private readonly object _engineGate = new();
    private IGemmaEngine? _engine;

    private readonly List<LiveChat> _chats = []; // mutated by the worker loop only; read under lock(_chats)
    private readonly ConcurrentQueue<string> _releases = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _loop;
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();
    private volatile bool _samplerDisabled;

    /// <summary>Creates the host for the real model. The engine itself loads at the first request.</summary>
    public GemmaEngineHost(GemmaEngineHostOptions options)
        : this(options, () => NativeGemmaEngine.Load(options), () => File.Exists(options.ModelPath))
    {
    }

    internal GemmaEngineHost(GemmaEngineHostOptions options, Func<IGemmaEngine> engineFactory, Func<bool>? isInstalled = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _isInstalled = isInstalled ?? (() => true);
        _loop = Task.Run(RunLoopAsync);
    }

    // ── ILlmHost ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool IsModelInstalled
    {
        get
        {
            try { return _isInstalled(); }
            catch { return false; }
        }
    }

    /// <inheritdoc />
    public int ContextTokens => _options.MaxNumTokens;

    /// <inheritdoc />
    public LlmHostStatus Status
    {
        get
        {
            lock (_lock)
                return ComputeStatusLocked();
        }
    }

    /// <inheritdoc />
    public event Action<LlmHostStatus>? StatusChanged;

    /// <summary>True while the engine is in memory (it loads lazily and unloads when idle).</summary>
    public bool IsEngineLoaded
    {
        get
        {
            lock (_engineGate)
                return _engine is not null;
        }
    }

    /// <summary>How long the last engine load took; null before the first load.</summary>
    public TimeSpan? LastLoadDuration { get; private set; }

    /// <summary>Number of live stateful chat conversations on the engine.</summary>
    public int LiveConversationCount
    {
        get
        {
            lock (_chats)
                return _chats.Count;
        }
    }

    /// <summary>
    /// Tokens held by the live conversation of <paramref name="conversationKey"/> as of its last completed send
    /// (non-blocking cache; 0 when there is none). Meant for a "context used" indicator.
    /// </summary>
    public int GetConversationTokenCount(string conversationKey)
    {
        lock (_chats)
        {
            var chat = _chats.Find(c => c.Key == conversationKey);
            return chat?.Conv.TokenCountCached ?? 0;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> GenerateStreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var chunk in GenerateStreamAsync(request, cancellationToken).ConfigureAwait(false))
            text.Append(chunk);
        return text.ToString();
    }

    /// <inheritdoc />
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return 0;

        try
        {
            lock (_engineGate)
            {
                if (_engine is not null)
                    return _engine.CountTokens(text);
            }
        }
        catch (ObjectDisposedException)
        {
        }

        // Not loaded: a conservative (high) estimate. Gemma averages 3-4 characters per token in English and
        // about 2 in Russian, so 2.5 over-counts a little instead of letting a prompt overflow.
        return (int)Math.Ceiling(text.Length / 2.5);
    }

    /// <inheritdoc />
    public void ReleaseConversation(string conversationKey)
    {
        ArgumentNullException.ThrowIfNull(conversationKey);
        _releases.Enqueue(conversationKey);
        Wake();
    }

    /// <summary>
    /// Loads the engine now (instead of at the first request). Throws <see cref="LlmUnavailableException"/> when the
    /// model is missing or fails to load.
    /// </summary>
    public Task PreloadAsync(CancellationToken cancellationToken = default, LlmPriority priority = LlmPriority.Background)
    {
        var job = new Job(priority, request: null, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        _ = job.Done.Task.ContinueWith(_ => job.Caller.Dispose(), TaskScheduler.Default);
        Enqueue(job);
        return job.Done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Synchronous <see cref="PreloadAsync"/> for constructors that must return a ready translator.</summary>
    public void Preload(LlmPriority priority = LlmPriority.Background) =>
        PreloadAsync(CancellationToken.None, priority).GetAwaiter().GetResult();

    /// <summary>Drops every live conversation and the engine once the host is idle. The next request reloads it.</summary>
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        var job = new Job(LlmPriority.Background, request: null, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Unload = true,
        };
        _ = job.Done.Task.ContinueWith(_ => job.Caller.Dispose(), TaskScheduler.Default);
        Enqueue(job);
        return job.Done.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        bool first;
        lock (_lock)
        {
            first = !_disposed;
            _disposed = true;
        }

        if (first)
        {
            try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
            Wake();
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // The loop reports failures to the requests; shutdown is best effort.
        }

        PublishStatus();
    }

    // ── Request side ─────────────────────────────────────────────────────────

    private async IAsyncEnumerable<string> StreamCoreAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var plan = Plan.From(request);
        ThrowIfUnavailable();

        var job = new Job(request.Priority, request, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            Plan = plan,
        };
        try
        {
            Enqueue(job);
            await foreach (var chunk in job.Out.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            // The caller stopped (or finished): stop generating and leave the queue. A no-op once the job is done.
            try { job.Caller.Cancel(); } catch (ObjectDisposedException) { }
            job.Caller.Dispose();
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (_lock)
        {
            if (_disposed)
                throw new LlmUnavailableException("The LLM host has been disposed.");
        }

        if (!IsModelInstalled)
            throw new LlmUnavailableException("The Gemma model is not installed.");
    }

    private void Enqueue(Job job)
    {
        job.Caller.Token.ThrowIfCancellationRequested();
        CancellationTokenSource? preempt = null;
        lock (_lock)
        {
            if (_disposed)
                throw new LlmUnavailableException("The LLM host has been disposed.");

            job.Seq = ++_seq;
            _queue.Add(job);
            job.CallerRegistration = job.Caller.Token.Register(() => OnCallerCancelled(job));

            var running = _running;
            if (running is { PreemptRequested: false } && job.Priority < running.Priority && running.Preempt is not null)
            {
                running.PreemptRequested = true;
                preempt = running.Preempt;
                _detail = job.Priority == LlmPriority.Live
                    ? "Waiting for the current generation to reach a cancel point (live translation is next)"
                    : "Waiting for the current generation to reach a cancel point";
            }
        }

        // Publish first: the status must show the wait before the cancellation lets the running request finish.
        PublishStatus();
        try { preempt?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
    }

    private void OnCallerCancelled(Job job)
    {
        bool removed;
        lock (_lock)
            removed = _queue.Remove(job);

        // Running jobs notice through their linked token; queued ones are finished here.
        if (removed)
        {
            job.Fail(new OperationCanceledException(job.Caller.Token));
            PublishStatus();
        }
    }

    // ── Worker loop (the lease) ──────────────────────────────────────────────

    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                DrainReleases();

                Job? job;
                bool disposed;
                lock (_lock)
                {
                    disposed = _disposed;
                    job = disposed ? null : PopNextLocked();
                }

                if (disposed)
                    break;

                if (job is null)
                {
                    if (!await WaitForWorkAsync().ConfigureAwait(false))
                        UnloadEngine();
                    continue;
                }

                PublishStatus();
                await ExecuteAsync(job).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GemmaEngineHost] worker loop crashed: {ex}");
        }
        finally
        {
            Shutdown();
        }
    }

    private Job? PopNextLocked()
    {
        Job? best = null;
        foreach (var candidate in _queue)
        {
            if (best is null || candidate.Priority < best.Priority
                || (candidate.Priority == best.Priority && candidate.Seq < best.Seq))
                best = candidate;
        }

        if (best is null)
            return null;

        _queue.Remove(best);
        best.Preempt = new CancellationTokenSource();
        best.PreemptRequested = false;
        _running = best;
        _detail = null;
        return best;
    }

    /// <summary>Waits for a wake-up; false means the idle-unload timeout elapsed with the engine loaded.</summary>
    private async Task<bool> WaitForWorkAsync()
    {
        TimeSpan? wait = null;
        if (_options.IdleUnloadAfter is { } idle && idle > TimeSpan.Zero && IsEngineLoaded)
        {
            var remaining = idle - Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivityTimestamp));
            if (remaining <= TimeSpan.Zero)
                return false;
            wait = remaining;
        }

        using var timeout = wait is { } w ? new CancellationTokenSource(w) : null;
        using var linked = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _disposeCts.Token);
        try
        {
            await _wake.Reader.WaitToReadAsync(linked?.Token ?? _disposeCts.Token).ConfigureAwait(false);
            _wake.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (timeout is { IsCancellationRequested: true })
        {
            lock (_lock)
            {
                if (_queue.Count > 0 || _disposed)
                    return true;
            }

            // Re-check: activity may have happened right at the deadline.
            return IsEngineLoaded
                   && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivityTimestamp)) < _options.IdleUnloadAfter!.Value;
        }
        catch (OperationCanceledException)
        {
            // Disposing.
        }

        return true;
    }

    private void Wake() => _wake.Writer.TryWrite(0);

    private void Touch() => Volatile.Write(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());

    private async Task ExecuteAsync(Job job)
    {
        var preempt = job.Preempt!;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Caller.Token, preempt.Token, _disposeCts.Token);
            var ct = linked.Token;
            try
            {
                if (job.Unload)
                {
                    UnloadEngine();
                    job.Complete();
                    return;
                }

                ct.ThrowIfCancellationRequested();
                var engine = await EnsureEngineAsync().ConfigureAwait(false);
                if (job.Request is null)
                {
                    job.Complete();
                    return;
                }

                await GenerateOnceAsync(job, engine, ct).ConfigureAwait(false);
                job.Complete();
            }
            catch (Exception ex)
            {
                HandleFailure(job, ex, preempt, ct);
            }
        }
        finally
        {
            lock (_lock)
            {
                _running = null;
                _detail = null;
                job.Preempt = null;
            }

            preempt.Dispose();
            DrainReleases();
            Touch();
            PublishStatus();
        }
    }

    private void HandleFailure(Job job, Exception ex, CancellationTokenSource preempt, CancellationToken ct)
    {
        bool disposed;
        lock (_lock)
            disposed = _disposed;

        if (disposed)
        {
            DropActive(job);
            job.Fail(new LlmUnavailableException("The LLM host has been disposed."));
            return;
        }

        if (job.Caller.IsCancellationRequested)
        {
            // Includes native errors that surface after the token fired.
            DropActive(job);
            job.Fail(new OperationCanceledException(job.Caller.Token));
            return;
        }

        if (preempt.IsCancellationRequested && (ex is OperationCanceledException || ct.IsCancellationRequested))
        {
            Requeue(job);
            return;
        }

        DropActive(job);
        job.Fail(ex);
    }

    private void Requeue(Job job)
    {
        if (job.Priority == LlmPriority.Interactive && job.Request is not null)
        {
            if (job.Segment.Length > 0)
            {
                // Continue later: capture the conversation (turns + the reply received so far) before dropping it.
                try
                {
                    job.Snapshot = job.ActiveConv!.Snapshot(job.Segment.ToString());
                }
                catch (Exception ex)
                {
                    DropActive(job);
                    job.Fail(new InvalidOperationException(
                        "The answer was interrupted by a higher-priority request and could not be continued.", ex));
                    return;
                }
            }
            else if (job.Emitted.Length > 0)
            {
                // Preempted again before the continuation produced text: resume from the same capture.
                job.Snapshot ??= job.ResumedFrom;
            }
        }

        // A second preemption before any new text arrived keeps the old snapshot (job.Snapshot is untouched).
        DropActive(job);
        job.Segment.Clear();
        if (job.Priority == LlmPriority.Background)
            job.Emitted.Clear();

        if (++job.Preemptions > MaxPreemptionsPerRequest)
        {
            job.Fail(new InvalidOperationException("The request was preempted too many times."));
            return;
        }

        lock (_lock)
            _queue.Add(job); // keeps its original Seq, so it stays ahead of newer requests of its class
        Wake();
    }

    private async Task<IGemmaEngine> EnsureEngineAsync()
    {
        lock (_engineGate)
        {
            if (_engine is not null)
                return _engine;
        }

        if (!IsModelInstalled)
            throw new LlmUnavailableException("The Gemma model is not installed.");

        lock (_lock)
            _detail = "Loading the model";
        PublishStatus();

        var sw = Stopwatch.StartNew();
        IGemmaEngine engine;
        try
        {
            engine = await Task.Run(_engineFactory).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var reason = "The model failed to load: " + ex.Message;
            lock (_lock)
                _unavailableReason = reason;
            throw new LlmUnavailableException(reason, ex);
        }

        LastLoadDuration = sw.Elapsed;
        lock (_engineGate)
            _engine = engine;
        lock (_lock)
        {
            _unavailableReason = null;
            _detail = null;
        }

        return engine;
    }

    private void UnloadEngine()
    {
        DropAllChats();
        IGemmaEngine? engine;
        lock (_engineGate)
        {
            engine = _engine;
            _engine = null;
        }

        try { engine?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[GemmaEngineHost] engine dispose: {ex.Message}"); }
        PublishStatus();
    }

    private void Shutdown()
    {
        List<Job> pending;
        lock (_lock)
        {
            _disposed = true;
            pending = [.. _queue];
            _queue.Clear();
        }

        foreach (var job in pending)
            job.Fail(new LlmUnavailableException("The LLM host has been disposed."));

        UnloadEngine();
        _disposeCts.Dispose();
    }

    // ── One request ──────────────────────────────────────────────────────────

    private async Task GenerateOnceAsync(Job job, IGemmaEngine engine, CancellationToken ct)
    {
        var request = job.Request!;
        var sampler = SamplerFor(request.Sampling);
        var sendSpec = new GemmaSendSpec(request.Priority == LlmPriority.Live ? _options.LiveNoRepeatNgramSize : 0);

        for (var attempt = 0; ; attempt++)
        {
            var useSampler = sampler is not null && !_samplerDisabled;
            var gotChunk = false;
            try
            {
                var continuing = job.Snapshot is not null;
                var (conversation, sendText, chat) = Acquire(job, engine, useSampler ? sampler : null);
                job.ActiveConv = conversation;
                job.ActiveChat = chat;
                var joiner = continuing ? new ContinuationJoiner(job.Emitted.ToString()) : null;

                await foreach (var raw in conversation.SendStreamingAsync(sendText, sendSpec, ct).ConfigureAwait(false))
                {
                    if (raw.Length == 0)
                        continue;

                    gotChunk = true;
                    Emit(job, joiner is null ? raw : joiner.Push(raw));
                }

                if (joiner is not null)
                    Emit(job, joiner.Flush());

                break;
            }
            catch (Exception ex) when (
                attempt == 0 && useSampler && !gotChunk && job.Emitted.Length == 0 && !ct.IsCancellationRequested
                && ex is not LlmContextOverflowException and not LlmUnavailableException)
            {
                // The CPU backend does not implement some samplers: a conversation asking for one fails its first send
                // and stays unusable. Retry once on a fresh conversation without it, and stop asking for the
                // rest of this engine's life (the model's own sampler applies).
                _samplerDisabled = true;
                DropActive(job);
            }
        }

        FinishConversation(job);
        if (job.Priority == LlmPriority.Background)
            job.Out.Writer.TryWrite(job.Emitted.ToString());
    }

    private static void Emit(Job job, string text)
    {
        if (text.Length == 0)
            return;

        job.Emitted.Append(text);
        job.Segment.Append(text);
        if (job.Priority != LlmPriority.Background)
            job.Out.Writer.TryWrite(text);
    }

    private (IGemmaConversation Conversation, string SendText, LiveChat? Chat) Acquire(
        Job job, IGemmaEngine engine, GemmaSamplerSpec? sampler)
    {
        var plan = job.Plan!;
        var key = job.Request!.ConversationKey;

        if (job.Snapshot is { } snapshot)
        {
            job.Snapshot = null;
            job.ResumedFrom = snapshot;
            var resumed = engine.Resume(snapshot);
            var resumedChat = key is null ? null : Register(key, resumed, plan.System, plan.History);
            return (resumed, _options.ContinuationPrompt, resumedChat);
        }

        if (key is not null)
        {
            LiveChat? existing;
            lock (_chats)
                existing = _chats.Find(c => c.Key == key);

            if (existing is not null)
            {
                if (plan.Matches(existing) && !existing.Conv.IsContextFull)
                {
                    lock (_chats)
                    {
                        _chats.Remove(existing);
                        _chats.Add(existing); // most recently used last
                    }

                    return (existing.Conv, plan.FinalUser, existing);
                }

                RemoveChat(existing);
            }
        }

        var maxOutput = job.Request.Sampling?.MaxOutputTokens is > 0 and var m ? m : _options.DefaultMaxOutputTokens;
        var created = engine.CreateConversation(new GemmaConversationSpec(plan.System, plan.History, maxOutput, sampler));
        var chat = key is null ? null : Register(key, created, plan.System, plan.History);
        return (created, plan.FinalUser, chat);
    }

    private LiveChat Register(string key, IGemmaConversation conversation, string? system, IReadOnlyList<LlmMessage> history)
    {
        var chat = new LiveChat(key, conversation, system, [.. history]);
        List<LiveChat> evicted = [];
        lock (_chats)
        {
            _chats.Add(chat);
            while (_chats.Count > Math.Max(1, _options.MaxLiveConversations))
            {
                evicted.Add(_chats[0]);
                _chats.RemoveAt(0);
            }
        }

        foreach (var old in evicted)
            SafeDispose(old.Conv);
        return chat;
    }

    private void FinishConversation(Job job)
    {
        var chat = job.ActiveChat;
        var conversation = job.ActiveConv;
        job.ActiveChat = null;
        job.ActiveConv = null;

        if (chat is null)
        {
            if (conversation is not null)
                SafeDispose(conversation);
            return;
        }

        var plan = job.Plan!;
        chat.History.Add(new LlmMessage(LlmRole.User, plan.FinalUser));
        chat.History.Add(new LlmMessage(LlmRole.Assistant, job.Emitted.ToString()));

        if (chat.Conv.IsContextFull)
            RemoveChat(chat);
    }

    /// <summary>Drops the conversation a failed or interrupted request was using (it may be dead).</summary>
    private void DropActive(Job job)
    {
        var chat = job.ActiveChat;
        var conversation = job.ActiveConv;
        job.ActiveChat = null;
        job.ActiveConv = null;

        if (chat is not null)
            RemoveChat(chat);
        else if (conversation is not null)
            SafeDispose(conversation);
    }

    private void RemoveChat(LiveChat chat)
    {
        bool removed;
        lock (_chats)
            removed = _chats.Remove(chat);
        if (removed)
            SafeDispose(chat.Conv);
    }

    private void DrainReleases()
    {
        while (_releases.TryDequeue(out var key))
        {
            LiveChat? chat;
            lock (_chats)
                chat = _chats.Find(c => c.Key == key);

            // A chat being generated right now is released when its request ends (the next drain).
            if (chat is not null)
            {
                bool inUse;
                lock (_lock)
                    inUse = _running?.ActiveChat == chat;
                if (inUse)
                {
                    _releases.Enqueue(key);
                    return;
                }

                RemoveChat(chat);
            }
        }
    }

    private void DropAllChats()
    {
        List<LiveChat> all;
        lock (_chats)
        {
            all = [.. _chats];
            _chats.Clear();
        }

        foreach (var chat in all)
            SafeDispose(chat.Conv);
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try { disposable.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[GemmaEngineHost] dispose: {ex.Message}"); }
    }

    private static GemmaSamplerSpec? SamplerFor(LlmSampling? sampling)
    {
        if (sampling is null || (sampling.Temperature is null && sampling.TopP is null))
            return null;

        return new GemmaSamplerSpec(Math.Max(0.01, sampling.Temperature ?? 1.0), sampling.TopP ?? 0.95);
    }

    // ── Status ───────────────────────────────────────────────────────────────

    private LlmHostStatus ComputeStatusLocked()
    {
        if (_disposed)
            return new LlmHostStatus(LlmHostState.Unavailable, null, 0, "The LLM host has been disposed");
        if (_running is { } running)
            return new LlmHostStatus(LlmHostState.Running, running.Priority, _queue.Count, _detail);
        if (_queue.Count > 0)
        {
            // Between two requests (or just queued, before the worker picks it up): report the next one as running
            // instead of flashing Idle.
            var next = _queue[0].Priority;
            foreach (var queued in _queue)
                next = (LlmPriority)Math.Min((int)next, (int)queued.Priority);
            return new LlmHostStatus(LlmHostState.Running, next, _queue.Count - 1, _detail);
        }

        if (_unavailableReason is not null)
            return new LlmHostStatus(LlmHostState.Unavailable, null, _queue.Count, _unavailableReason);
        if (!IsModelInstalled)
            return new LlmHostStatus(LlmHostState.Unavailable, null, 0, "The Gemma model is not installed");
        return new LlmHostStatus(LlmHostState.Idle, null, _queue.Count);
    }

    private void PublishStatus()
    {
        LlmHostStatus status;
        lock (_lock)
        {
            status = ComputeStatusLocked();
            if (status == _lastPublished)
                return;
            _lastPublished = status;
        }

        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { Debug.WriteLine($"[GemmaEngineHost] StatusChanged handler: {ex.Message}"); }
    }

    // ── Types ────────────────────────────────────────────────────────────────

    private sealed class LiveChat(string key, IGemmaConversation conv, string? system, List<LlmMessage> history)
    {
        public string Key { get; } = key;
        public IGemmaConversation Conv { get; } = conv;
        public string? System { get; } = system;

        /// <summary>The non-system turns the conversation holds (what the caller's history must match).</summary>
        public List<LlmMessage> History { get; } = history;
    }

    /// <summary>A request split into system prompt, earlier turns and the final user message.</summary>
    private sealed class Plan
    {
        public string? System { get; private init; }
        public IReadOnlyList<LlmMessage> History { get; private init; } = [];
        public string FinalUser { get; private init; } = "";

        public static Plan From(LlmRequest request)
        {
            var system = new StringBuilder();
            var turns = new List<LlmMessage>();
            foreach (var message in request.Messages)
            {
                if (message.Role == LlmRole.System)
                {
                    if (system.Length > 0)
                        system.Append("\n\n");
                    system.Append(message.Text);
                }
                else
                {
                    turns.Add(message);
                }
            }

            if (turns.Count == 0 || turns[^1].Role != LlmRole.User)
                throw new ArgumentException("The last message of a request must be a user message.", nameof(request));

            return new Plan
            {
                System = system.Length > 0 ? system.ToString() : null,
                History = turns.GetRange(0, turns.Count - 1),
                FinalUser = turns[^1].Text,
            };
        }

        public bool Matches(LiveChat chat)
        {
            if (!string.Equals(System, chat.System, StringComparison.Ordinal) || History.Count != chat.History.Count)
                return false;

            for (var i = 0; i < History.Count; i++)
            {
                if (History[i].Role != chat.History[i].Role
                    || !string.Equals(History[i].Text.Trim(), chat.History[i].Text.Trim(), StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }

    private sealed class Job(LlmPriority priority, LlmRequest? request, CancellationTokenSource caller)
    {
        public LlmPriority Priority { get; } = priority;
        public LlmRequest? Request { get; } = request;
        public CancellationTokenSource Caller { get; } = caller;
        public Channel<string> Out { get; } = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public long Seq { get; set; }
        public Plan? Plan { get; init; }
        public TaskCompletionSource? Done { get; init; }
        public bool Unload { get; init; }
        public CancellationTokenRegistration CallerRegistration { get; set; }

        // Set while running (under the host lock).
        public CancellationTokenSource? Preempt { get; set; }
        public bool PreemptRequested { get; set; }

        // Worker-loop state.
        public StringBuilder Emitted { get; } = new();
        public StringBuilder Segment { get; } = new();
        public IGemmaSnapshot? Snapshot { get; set; }
        public IGemmaSnapshot? ResumedFrom { get; set; }
        public IGemmaConversation? ActiveConv { get; set; }
        public LiveChat? ActiveChat { get; set; }
        public int Preemptions { get; set; }

        private int _finished;

        public void Complete()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1)
                return;
            CallerRegistration.Dispose();
            Out.Writer.TryComplete();
            Done?.TrySetResult();
        }

        public void Fail(Exception ex)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1)
                return;
            CallerRegistration.Dispose();
            Out.Writer.TryComplete(ex);
            Done?.TrySetException(ex);
        }
    }
}
