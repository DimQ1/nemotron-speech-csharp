using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native.Tests;

public sealed class Unit_GemmaEngineHostTests
{
    private static GemmaEngineHost NewHost(
        FakeGemmaEngine engine,
        TimeSpan? idleUnload = null,
        int maxLive = 3,
        Func<bool>? installed = null,
        Action? onLoad = null) =>
        new(
            new GemmaEngineHostOptions { ModelPath = "fake", IdleUnloadAfter = idleUnload, MaxLiveConversations = maxLive },
            () =>
            {
                onLoad?.Invoke();
                return engine;
            },
            installed);

    private static LlmRequest Req(LlmPriority priority, string text, string? key = null, params LlmMessage[] before)
    {
        var messages = new List<LlmMessage> { new(LlmRole.System, "sys") };
        messages.AddRange(before);
        messages.Add(new LlmMessage(LlmRole.User, text));
        return new LlmRequest(priority, messages, ConversationKey: key);
    }

    private static async Task<string> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var chunk in stream)
            text.Append(chunk);
        return text.ToString();
    }

    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Generate_ShouldReturnWholeAnswerAndStreamChunks()
    {
        var engine = new FakeGemmaEngine { ReplyFor = _ => FakeReply.Text("Hel", "lo") };
        await using var host = NewHost(engine);

        var chunks = new List<string>();
        await foreach (var c in host.GenerateStreamAsync(Req(LlmPriority.Interactive, "hi")))
            chunks.Add(c);

        Assert.Equal(["Hel", "lo"], chunks);
        Assert.Equal("Hello", await host.GenerateAsync(Req(LlmPriority.Live, "again")));
    }

    [Fact]
    public async Task Scheduler_ShouldRunHigherClassFirstAndFifoInsideClass()
    {
        var order = new List<string>();
        FakeReply? holder = null;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call =>
            {
                lock (order)
                    order.Add(call.UserText);
                if (call.UserText != "hold")
                    return FakeReply.Text("x");
                return holder = FakeReply.Held(0, "x");
            },
        };
        await using var host = NewHost(engine);

        // A Live request occupies the engine; Live cannot be preempted by Live, Interactive or Background.
        var hold = host.GenerateAsync(Req(LlmPriority.Live, "hold"));
        await WaitUntil(() => holder?.Reached.Task.IsCompleted == true);

        var tasks = new[]
        {
            host.GenerateAsync(Req(LlmPriority.Background, "bg1")),
            host.GenerateAsync(Req(LlmPriority.Background, "bg2")),
            host.GenerateAsync(Req(LlmPriority.Interactive, "chat1")),
            host.GenerateAsync(Req(LlmPriority.Interactive, "chat2")),
            host.GenerateAsync(Req(LlmPriority.Live, "live2")),
        };
        await WaitUntil(() => host.Status.QueuedCount == 5);
        holder!.Release.SetResult();
        await Task.WhenAll(tasks.Append(hold));

        Assert.Equal(["hold", "live2", "chat1", "chat2", "bg1", "bg2"], order);
        Assert.Equal(1, engine.MaxInFlight);
    }

    [Fact]
    public async Task Live_ShouldPreemptBackgroundWhichIsRerunWhole()
    {
        var order = new List<string>();
        var bgCalls = 0;
        FakeReply? firstBackground = null;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call =>
            {
                lock (order)
                    order.Add(call.UserText);
                if (call.UserText == "bg" && Interlocked.Increment(ref bgCalls) == 1)
                    return firstBackground = FakeReply.Held(1, "a", "b", "c");
                return call.UserText == "bg" ? FakeReply.Text("a", "b", "c") : FakeReply.Text("live-ok");
            },
        };
        await using var host = NewHost(engine);

        var background = host.GenerateAsync(Req(LlmPriority.Background, "bg"));
        await WaitUntil(() => firstBackground?.Reached.Task.IsCompleted == true);
        var live = await host.GenerateAsync(Req(LlmPriority.Live, "live"));
        var bgText = await background;

        Assert.Equal("live-ok", live);
        Assert.Equal("abc", bgText); // whole text of the re-run, no duplicates from the cancelled attempt
        Assert.Equal(["bg", "live", "bg"], order);
        Assert.True(engine.Conversations[0].Disposed); // the cancelled conversation is dead and released
        Assert.Equal(1, engine.MaxInFlight);
    }

    [Fact]
    public async Task Live_ShouldPreemptInteractiveWhichContinuesWithoutDuplicates()
    {
        FakeReply? first = null;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call =>
            {
                if (call.UserText == "question")
                    return first = FakeReply.Held(2, "he", "llo ", "wor", "ld");
                if (call.Conversation.IsResumed)
                    return FakeReply.Text("wor", "ld");
                return FakeReply.Text("live-ok");
            },
        };
        await using var host = NewHost(engine);

        var received = new List<string>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var c in host.GenerateStreamAsync(Req(LlmPriority.Interactive, "question")))
                lock (received)
                    received.Add(c);
        });
        await WaitUntil(() => first?.Reached.Task.IsCompleted == true);

        var live = await host.GenerateAsync(Req(LlmPriority.Live, "live"));
        await consumer;

        Assert.Equal("live-ok", live);
        Assert.Equal("hello world", string.Concat(received));
        var resumed = Assert.Single(engine.Resumed);
        Assert.Equal("hello ", resumed.ResumedPartial);
        Assert.Equal(new GemmaEngineHostOptions { ModelPath = "x" }.ContinuationPrompt, resumed.Sends[0]);
        Assert.Equal(1, engine.MaxInFlight);
    }

    [Fact]
    public async Task Interactive_ShouldRestartWhenPreemptedBeforeAnyText()
    {
        FakeReply? first = null;
        var calls = 0;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call =>
            {
                if (call.UserText == "question" && Interlocked.Increment(ref calls) == 1)
                    return first = FakeReply.Held(0, "a", "b");
                return call.UserText == "question" ? FakeReply.Text("a", "b") : FakeReply.Text("live-ok");
            },
        };
        await using var host = NewHost(engine);

        var chat = CollectAsync(host.GenerateStreamAsync(Req(LlmPriority.Interactive, "question")));
        await WaitUntil(() => first?.Reached.Task.IsCompleted == true);
        await host.GenerateAsync(Req(LlmPriority.Live, "live"));

        Assert.Equal("ab", await chat);
        Assert.Empty(engine.Resumed);
    }

    [Fact]
    public async Task Interactive_ShouldKeepStatefulConversationAfterContinuation()
    {
        FakeReply? first = null;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call =>
            {
                if (call.UserText == "q1" && !call.Conversation.IsResumed)
                    return first = FakeReply.Held(1, "he", "llo");
                if (call.Conversation.IsResumed)
                    return FakeReply.Text("llo");
                return FakeReply.Text("R(" + call.UserText + ")");
            },
        };
        await using var host = NewHost(engine);

        var chat = CollectAsync(host.GenerateStreamAsync(Req(LlmPriority.Interactive, "q1", "chat")));
        await WaitUntil(() => first?.Reached.Task.IsCompleted == true);
        await host.GenerateAsync(Req(LlmPriority.Live, "live"));
        Assert.Equal("hello", await chat);

        // The follow-up reuses the resumed conversation: the caller's history is [user q1, assistant "hello"].
        var created = engine.Conversations.Count;
        await host.GenerateAsync(Req(LlmPriority.Interactive, "q2", "chat",
            new LlmMessage(LlmRole.User, "q1"), new LlmMessage(LlmRole.Assistant, "hello")));
        Assert.Equal(created, engine.Conversations.Count);
    }

    [Fact]
    public async Task Cancel_ShouldStopRunningRequestAndEngineStaysUsable()
    {
        FakeReply? held = null;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call => call.UserText == "slow" ? held = FakeReply.Held(1, "a", "b") : FakeReply.Text("fine"),
        };
        await using var host = NewHost(engine);

        using var cts = new CancellationTokenSource();
        var slow = host.GenerateAsync(Req(LlmPriority.Interactive, "slow", "chat"), cts.Token);
        await WaitUntil(() => held?.Reached.Task.IsCompleted == true);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

        Assert.Equal("fine", await host.GenerateAsync(Req(LlmPriority.Interactive, "next", "chat")));
        Assert.True(engine.Conversations[0].Disposed); // the dead chat conversation was dropped, not reused
        Assert.Equal(1, engine.MaxInFlight);
    }

    [Fact]
    public async Task AbandonedStream_ShouldNotKillTheHost()
    {
        // The consumer of a stream stops (live translation superseding/flushing a sentence, a chat Stop): the stream's
        // finally cancels and DISPOSES the request's token source while the worker is still inside the native send.
        // The worker then reports the cancellation from that disposed source; it used to throw out of the loop,
        // which shut the host down for good ("The LLM host has been disposed.").
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ManualResetEventSlim();
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call => call.UserText == "slow"
                ? new FakeReply
                {
                    FailWith = () =>
                    {
                        inSend.TrySetResult();
                        gate.Wait(); // the worker is busy while the consumer walks away
                        return new InvalidOperationException("CANCELLED: native send aborted");
                    },
                }
                : FakeReply.Text("fine"),
        };
        await using var host = NewHost(engine);

        using var cts = new CancellationTokenSource();
        var slow = Task.Run(async () =>
        {
            await foreach (var _ in host.GenerateStreamAsync(Req(LlmPriority.Live, "slow"), cts.Token))
            {
            }
        });
        await inSend.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow); // its finally disposed the request's token source
        gate.Set();

        Assert.Equal("fine", await host.GenerateAsync(Req(LlmPriority.Background, "next")).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(LlmHostState.Unavailable, host.Status.State);
    }

    [Fact]
    public async Task SharedHost_ShouldIgnoreDisposeFromComponents()
    {
        var engine = new FakeGemmaEngine();
        await using var real = NewHost(engine);
        var ignored = new List<string>();
        var shared = new SharedLlmHost(real) { OnIgnoredDispose = ignored.Add };

        await shared.DisposeAsync();
        await shared.DisposeAsync();

        Assert.Equal(2, shared.IgnoredDisposeCount);
        Assert.Equal(2, ignored.Count);
        Assert.Equal("R(hi)", await shared.GenerateAsync(Req(LlmPriority.Interactive, "hi")));
        Assert.NotEqual(LlmHostState.Unavailable, shared.Status.State);
    }

    [Fact]
    public async Task Diagnostics_ShouldReportWhoDisposedTheHost()
    {
        var lines = new List<string>();
        var host = new GemmaEngineHost(
            new GemmaEngineHostOptions { ModelPath = "fake", IdleUnloadAfter = null, Diagnostics = lines.Add },
            () => new FakeGemmaEngine());

        await host.DisposeAsync();

        Assert.Contains(lines, l => l.Contains("DisposeAsync called by") && l.Contains(nameof(Diagnostics_ShouldReportWhoDisposedTheHost)));
    }

    [Fact]
    public async Task Cancel_ShouldRemoveQueuedRequestWithoutRunningIt()
    {
        FakeReply? hold = null;
        var sent = new List<string>();
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call =>
            {
                lock (sent)
                    sent.Add(call.UserText);
                return call.UserText == "hold" ? hold = FakeReply.Held(0, "x") : FakeReply.Text("x");
            },
        };
        await using var host = NewHost(engine);

        var holder = host.GenerateAsync(Req(LlmPriority.Live, "hold"));
        await WaitUntil(() => hold?.Reached.Task.IsCompleted == true);
        using var cts = new CancellationTokenSource();
        var queued = host.GenerateAsync(Req(LlmPriority.Background, "queued"), cts.Token);
        await WaitUntil(() => host.Status.QueuedCount == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await WaitUntil(() => host.Status.QueuedCount == 0);

        hold!.Release.SetResult();
        await holder;
        Assert.Equal(["hold"], sent);
    }

    [Fact]
    public async Task NativeErrorAfterCancellation_ShouldSurfaceAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var engine = new FakeGemmaEngine
        {
            ReplyFor = _ => new FakeReply
            {
                FailWith = () =>
                {
                    cts.Cancel();
                    return new InvalidOperationException("CANCELLED");
                },
            },
        };
        await using var host = NewHost(engine);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.GenerateAsync(Req(LlmPriority.Interactive, "x"), cts.Token));
    }

    [Fact]
    public async Task Conversation_ShouldBeReusedWhenHistoryMatchesAndRebuiltWhenNot()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewHost(engine);

        await host.GenerateAsync(Req(LlmPriority.Interactive, "hi", "c1"));
        var conversation = Assert.Single(engine.Conversations);

        // Whitespace differences in the stored assistant text do not break the match.
        await host.GenerateAsync(Req(LlmPriority.Interactive, "more", "c1",
            new LlmMessage(LlmRole.User, "hi"), new LlmMessage(LlmRole.Assistant, " R(hi)\n")));
        Assert.Single(engine.Conversations);
        Assert.Equal(["hi", "more"], conversation.Sends); // only the new turn was sent
        Assert.False(conversation.Disposed);

        // The caller edited the history: replay on a new conversation.
        await host.GenerateAsync(Req(LlmPriority.Interactive, "third", "c1",
            new LlmMessage(LlmRole.User, "different"), new LlmMessage(LlmRole.Assistant, "x")));
        Assert.Equal(2, engine.Conversations.Count);
        Assert.True(conversation.Disposed);
        var rebuilt = engine.Conversations[1];
        Assert.Equal(["different", "x"], rebuilt.Spec.History.Select(m => m.Text));
        Assert.Equal(["third"], rebuilt.Sends);
    }

    [Fact]
    public async Task Conversation_ShouldEvictLeastRecentlyUsedBeyondLimit()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewHost(engine, maxLive: 3);

        foreach (var key in new[] { "a", "b", "c" })
            await host.GenerateAsync(Req(LlmPriority.Interactive, "hi", key));
        await host.GenerateAsync(Req(LlmPriority.Interactive, "more", "a",
            new LlmMessage(LlmRole.User, "hi"), new LlmMessage(LlmRole.Assistant, "R(hi)"))); // "a" is now most recent
        await host.GenerateAsync(Req(LlmPriority.Interactive, "hi", "d"));

        Assert.Equal(3, host.LiveConversationCount);
        Assert.False(engine.Conversations[0].Disposed); // "a" survived: it was used last before "d"
        Assert.True(engine.Conversations[1].Disposed); // "b" was the least recently used
    }

    [Fact]
    public async Task ReleaseConversation_ShouldDropLiveConversation()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewHost(engine);
        await host.GenerateAsync(Req(LlmPriority.Interactive, "hi", "c1"));
        Assert.Equal(1, host.LiveConversationCount);

        host.ReleaseConversation("c1");

        await WaitUntil(() => engine.Conversations[0].Disposed);
        Assert.Equal(0, host.LiveConversationCount);
    }

    [Fact]
    public async Task ContextFull_ShouldDropConversationSoNextTurnReplays()
    {
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call =>
            {
                call.Conversation.FullFlag = true;
                return FakeReply.Text("R");
            },
        };
        await using var host = NewHost(engine);

        await host.GenerateAsync(Req(LlmPriority.Interactive, "hi", "c1"));
        Assert.True(engine.Conversations[0].Disposed);
        await host.GenerateAsync(Req(LlmPriority.Interactive, "more", "c1",
            new LlmMessage(LlmRole.User, "hi"), new LlmMessage(LlmRole.Assistant, "R")));

        Assert.Equal(2, engine.Conversations.Count);
        Assert.Equal(2, engine.Conversations[1].Spec.History.Count);
    }

    [Fact]
    public async Task Stateless_ShouldUseFreshConversationPerRequestAndReleaseIt()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewHost(engine);

        await host.GenerateAsync(new LlmRequest(LlmPriority.Live,
            [new(LlmRole.System, "s"), new(LlmRole.User, "p"), new(LlmRole.Assistant, "pt"), new(LlmRole.User, "now")]));
        await host.GenerateAsync(Req(LlmPriority.Live, "again"));

        Assert.Equal(2, engine.Conversations.Count);
        Assert.All(engine.Conversations, c => Assert.True(c.Disposed));
        Assert.Equal("s", engine.Conversations[0].Spec.System);
        Assert.Equal(["p", "pt"], engine.Conversations[0].Spec.History.Select(m => m.Text));
        Assert.Equal(8, engine.Conversations[0].SendSpecs[0].NoRepeatNgram); // Live replies are protected from loops
        Assert.Equal(0, host.LiveConversationCount);
    }

    [Fact]
    public async Task Sampler_ShouldRetryOnceWithoutExplicitSamplerAndStopAsking()
    {
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call => call.Conversation.Spec.Sampler is null
                ? FakeReply.Text("ok")
                : new FakeReply { FailWith = () => new GemmaSamplerException("Sampler type: 3 not implemented") },
        };
        await using var host = NewHost(engine);
        var sampled = new LlmSampling(Temperature: 0.1, TopP: 0.5);

        Assert.Equal("ok", await host.GenerateAsync(Req(LlmPriority.Live, "a") with { Sampling = sampled }));
        Assert.Equal(2, engine.Conversations.Count);
        Assert.NotNull(engine.Conversations[0].Spec.Sampler);
        Assert.Null(engine.Conversations[1].Spec.Sampler);

        Assert.Equal("ok", await host.GenerateAsync(Req(LlmPriority.Live, "b") with { Sampling = sampled }));
        Assert.Equal(3, engine.Conversations.Count); // no failing attempt this time
        Assert.Null(engine.Conversations[2].Spec.Sampler);
    }

    [Fact]
    public async Task NoSamplingHints_ShouldNotRequestAnExplicitSampler()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewHost(engine);

        await host.GenerateAsync(Req(LlmPriority.Live, "a") with { Sampling = new LlmSampling(MaxOutputTokens: 77) });

        Assert.Null(engine.Conversations[0].Spec.Sampler);
        Assert.Equal(77, engine.Conversations[0].Spec.MaxOutputTokens);
    }

    [Fact]
    public async Task ContextOverflow_ShouldReachTheCallerAsLlmContextOverflowException()
    {
        var engine = new FakeGemmaEngine
        {
            ReplyFor = _ => new FakeReply { FailWith = () => new LlmContextOverflowException("too long", 5000, 4096) },
        };
        await using var host = NewHost(engine);

        var ex = await Assert.ThrowsAsync<LlmContextOverflowException>(
            () => host.GenerateAsync(Req(LlmPriority.Interactive, "x", "c1")));

        Assert.Equal(4096, ex.MaxTokens);
        Assert.Equal(0, host.LiveConversationCount);
    }

    [Fact]
    public async Task MissingModel_ShouldThrowUnavailableAndReportStatus()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewHost(engine, installed: () => false);

        Assert.False(host.IsModelInstalled);
        Assert.Equal(LlmHostState.Unavailable, host.Status.State);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => host.GenerateAsync(Req(LlmPriority.Live, "x")));
        Assert.False(host.IsEngineLoaded);
    }

    [Fact]
    public async Task LoadFailure_ShouldReportUnavailableThenRetryOnNextRequest()
    {
        var engine = new FakeGemmaEngine();
        var attempts = 0;
        await using var host = NewHost(engine, onLoad: () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("not enough memory");
        });

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => host.GenerateAsync(Req(LlmPriority.Live, "x")));
        Assert.Contains("not enough memory", ex.Message);
        Assert.Equal(LlmHostState.Unavailable, host.Status.State);

        Assert.Equal("R(y)", await host.GenerateAsync(Req(LlmPriority.Live, "y")));
        Assert.Equal(LlmHostState.Idle, host.Status.State);
    }

    [Fact]
    public async Task Status_ShouldReportRunningPriorityQueueAndReturnToIdle()
    {
        FakeReply? hold = null;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call => call.UserText == "chat" ? hold = FakeReply.Held(1, "a", "b") : FakeReply.Text("x"),
        };
        await using var host = NewHost(engine);
        var events = new List<LlmHostStatus>();
        host.StatusChanged += s =>
        {
            lock (events)
                events.Add(s);
        };

        var chat = host.GenerateAsync(Req(LlmPriority.Interactive, "chat"));
        await WaitUntil(() => hold?.Reached.Task.IsCompleted == true);
        Assert.Equal(LlmHostState.Running, host.Status.State);
        Assert.Equal(LlmPriority.Interactive, host.Status.RunningPriority);

        var live = host.GenerateAsync(Req(LlmPriority.Live, "live"));
        await Task.WhenAll(chat, live);
        await WaitUntil(() => host.Status.State == LlmHostState.Idle);

        LlmHostStatus[] seen;
        lock (events)
            seen = [.. events];
        Assert.Contains(seen, s => s.State == LlmHostState.Running && s.RunningPriority == LlmPriority.Interactive);
        Assert.Contains(seen, s => s.RunningPriority == LlmPriority.Interactive && s.QueuedCount == 1
                                   && s.Detail is not null && s.Detail.Contains("cancel point"));
        Assert.Contains(seen, s => s.RunningPriority == LlmPriority.Live);
        Assert.Equal(LlmHostState.Idle, seen[^1].State);
    }

    [Fact]
    public async Task IdleUnload_ShouldReleaseEngineAndReloadLazily()
    {
        var engines = new List<FakeGemmaEngine>();
        var host = new GemmaEngineHost(
            new GemmaEngineHostOptions { ModelPath = "fake", IdleUnloadAfter = TimeSpan.FromMilliseconds(150) },
            () =>
            {
                var e = new FakeGemmaEngine();
                lock (engines)
                    engines.Add(e);
                return e;
            });
        await using var _ = host;

        await host.GenerateAsync(Req(LlmPriority.Interactive, "hi", "c1"));
        Assert.True(host.IsEngineLoaded);

        await WaitUntil(() => engines[0].Disposed);
        Assert.False(host.IsEngineLoaded);
        Assert.True(engines[0].Conversations[0].Disposed);
        Assert.Equal(0, host.LiveConversationCount);

        Assert.Equal("R(again)", await host.GenerateAsync(Req(LlmPriority.Live, "again")));
        Assert.Equal(2, engines.Count);
    }

    private static GemmaEngineHost NewIdleHost(FakeGemmaEngine engine, TimeSpan? idle, TimeSpan? background) =>
        new(new GemmaEngineHostOptions { ModelPath = "fake", IdleUnloadAfter = idle, BackgroundIdleUnloadAfter = background }, () => engine);

    [Fact]
    public async Task IdleUnload_AfterBackgroundWork_ShouldUseTheShorterBackgroundTimeout()
    {
        // The title of a saved recording was the last thing asked: nobody waits for the model, so it goes soon.
        var engine = new FakeGemmaEngine();
        await using var host = NewIdleHost(engine, idle: TimeSpan.FromMinutes(5), background: TimeSpan.FromMilliseconds(150));

        await host.GenerateAsync(Req(LlmPriority.Background, "title"));
        Assert.True(host.IsEngineLoaded);

        await WaitUntil(() => engine.Disposed);
        Assert.False(host.IsEngineLoaded);
    }

    [Theory]
    [InlineData(LlmPriority.Live)]
    [InlineData(LlmPriority.Interactive)]
    public async Task IdleUnload_AfterLiveOrChatWork_ShouldKeepTheEngineForTheLongerTimeout(LlmPriority priority)
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewIdleHost(engine, idle: TimeSpan.FromMinutes(5), background: TimeSpan.FromMilliseconds(100));

        await host.GenerateAsync(Req(priority, "hi"));
        await Task.Delay(700);

        Assert.True(host.IsEngineLoaded);
        Assert.False(engine.Disposed);
    }

    [Fact]
    public async Task IdleUnload_ABackgroundJobBeforeAChatTurn_ShouldNotShortenTheChatsTimeout()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewIdleHost(engine, idle: TimeSpan.FromMinutes(5), background: TimeSpan.FromMilliseconds(100));

        await host.GenerateAsync(Req(LlmPriority.Background, "title"));
        await host.GenerateAsync(Req(LlmPriority.Interactive, "question", "c1"));   // the last request decides
        await Task.Delay(700);

        Assert.True(host.IsEngineLoaded);
    }

    [Fact]
    public async Task IdleUnload_WhenNeverUnloadIsSet_ShouldIgnoreTheBackgroundTimeout()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewIdleHost(engine, idle: null, background: TimeSpan.FromMilliseconds(100));

        await host.GenerateAsync(Req(LlmPriority.Background, "title"));
        await Task.Delay(700);

        Assert.True(host.IsEngineLoaded);
    }

    [Fact]
    public async Task IdleUnload_ABackgroundTimeoutLongerThanTheGeneralOne_ShouldNotExtendIt()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewIdleHost(engine, idle: TimeSpan.FromMilliseconds(150), background: TimeSpan.FromMinutes(5));

        await host.GenerateAsync(Req(LlmPriority.Background, "title"));

        await WaitUntil(() => engine.Disposed);
    }

    [Fact]
    public async Task Dispose_ShouldFailPendingRequestsAndReleaseEverything()
    {
        FakeReply? hold = null;
        var engine = new FakeGemmaEngine
        {
            ReplyFor = call => call.UserText == "hold" ? hold = FakeReply.Held(0, "x") : FakeReply.Text("x"),
        };
        var host = NewHost(engine);

        await host.GenerateAsync(Req(LlmPriority.Interactive, "warm", "c1"));
        var running = host.GenerateAsync(Req(LlmPriority.Interactive, "hold", "c2"));
        await WaitUntil(() => hold?.Reached.Task.IsCompleted == true);
        var queued = host.GenerateAsync(Req(LlmPriority.Background, "queued"));
        await WaitUntil(() => host.Status.QueuedCount == 1);

        await host.DisposeAsync();

        await Assert.ThrowsAsync<LlmUnavailableException>(() => running);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => queued);
        Assert.True(engine.Disposed);
        Assert.All(engine.Conversations, c => Assert.True(c.Disposed));
        await Assert.ThrowsAsync<LlmUnavailableException>(() => host.GenerateAsync(Req(LlmPriority.Live, "late")));
        await host.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task CountTokens_ShouldUseEngineWhenLoadedAndEstimateOtherwise()
    {
        var engine = new FakeGemmaEngine();
        await using var host = NewHost(engine);

        var estimate = host.CountTokens("0123456789");
        Assert.Equal(4, estimate);

        await host.GenerateAsync(Req(LlmPriority.Live, "x"));
        Assert.Equal(engine.CountTokens("0123456789"), host.CountTokens("0123456789"));
        Assert.Equal(0, host.CountTokens(""));
    }

    [Fact]
    public async Task MixedLoad_ShouldNeverOverlapNativeOperationsAndCompleteEverything()
    {
        // An Interactive request that a higher class preempts is resumed on a fresh conversation, which continues the
        // answer (like the real model asked to "continue"); a fake that restarted it would give "aabcd" whenever a
        // preemption lands after the first chunk, and the host does not trim an overlap shorter than 6 characters.
        string[] full = ["a", "b", "c", "d"];
        var engine = new FakeGemmaEngine
        {
            ChunkDelay = TimeSpan.FromMilliseconds(1),
            ReplyFor = call => FakeReply.Text(call.Conversation.IsResumed ? full[(call.Conversation.ResumedPartial?.Length ?? 0)..] : full),
        };
        await using var host = NewHost(engine);
        var random = new Random(7);

        var tasks = Enumerable.Range(0, 100).Select(i =>
        {
            var priority = (LlmPriority)random.Next(3);
            var key = priority == LlmPriority.Interactive ? "k" + random.Next(4) : null;
            return Task.Run(() => host.GenerateAsync(Req(priority, "m" + i, key)));
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("abcd", r));
        Assert.Equal(1, engine.MaxInFlight);
        Assert.True(host.LiveConversationCount <= 3);
    }

    [Fact]
    public async Task Request_ShouldRejectMissingFinalUserMessage()
    {
        await using var host = NewHost(new FakeGemmaEngine());

        await Assert.ThrowsAsync<ArgumentException>(() => host.GenerateAsync(
            new LlmRequest(LlmPriority.Live, [new LlmMessage(LlmRole.System, "s")])));
    }
}
