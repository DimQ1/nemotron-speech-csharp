using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native.Tests;

/// <summary>Runtime model / backend / context switch of the shared host.</summary>
public sealed class Unit_GemmaEngineHostReconfigureTests
{
    private sealed class Rig
    {
        public List<FakeGemmaEngine> Engines { get; } = [];
        public List<GemmaEngineHostOptions> LoadedWith { get; } = [];
        public Func<FakeGemmaEngine> NewEngine { get; set; } = () => new FakeGemmaEngine();
        public HashSet<string> MissingPaths { get; } = [];

        public GemmaEngineHost NewHost(string path = "small.litertlm", int context = 4096) =>
            new(new GemmaEngineHostOptions { ModelPath = path, MaxNumTokens = context, IdleUnloadAfter = null },
                options =>
                {
                    var engine = NewEngine();
                    lock (Engines)
                    {
                        Engines.Add(engine);
                        LoadedWith.Add(options);
                    }

                    return engine;
                },
                options => !MissingPaths.Contains(options.ModelPath));
    }

    private static LlmRequest Req(LlmPriority priority, string text, string? key = null) =>
        new(priority, [new LlmMessage(LlmRole.System, "sys"), new LlmMessage(LlmRole.User, text)], ConversationKey: key);

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
    public async Task Reconfigure_ShouldChangeContextTokensAndSettingsAndLoadNewEngineLazily()
    {
        var rig = new Rig();
        await using var host = rig.NewHost();
        Assert.Equal(4096, host.ContextTokens);

        await host.ReconfigureAsync(new LlmHostSettings("big.litertlm", "GPU", 8192));

        Assert.Equal(8192, host.ContextTokens);
        Assert.Equal(new LlmHostSettings("big.litertlm", "gpu", 8192), host.Settings);
        Assert.Empty(rig.Engines); // nothing loaded until the next request

        Assert.Equal("R(hi)", await host.GenerateAsync(Req(LlmPriority.Interactive, "hi")));
        var loaded = Assert.Single(rig.LoadedWith);
        Assert.Equal("big.litertlm", loaded.ModelPath);
        Assert.Equal("gpu", loaded.Backend);
        Assert.Equal(8192, loaded.MaxNumTokens);
        Assert.Null(loaded.IdleUnloadAfter); // the other options are carried over
    }

    [Fact]
    public async Task Reconfigure_WhileIdle_ShouldUnloadOldEngineAndReloadWithNewSettings()
    {
        var rig = new Rig();
        await using var host = rig.NewHost();
        await host.GenerateAsync(Req(LlmPriority.Interactive, "one"));
        Assert.True(host.IsEngineLoaded);

        await host.ReconfigureAsync(new LlmHostSettings("big.litertlm", "cpu", 8192));

        Assert.False(host.IsEngineLoaded);
        Assert.True(rig.Engines[0].Disposed);

        await host.GenerateAsync(Req(LlmPriority.Interactive, "two"));
        Assert.Equal(2, rig.Engines.Count);
        Assert.Equal("small.litertlm", rig.LoadedWith[0].ModelPath);
        Assert.Equal("big.litertlm", rig.LoadedWith[1].ModelPath);
        Assert.Equal(8192, rig.LoadedWith[1].MaxNumTokens);
    }

    [Fact]
    public async Task Reconfigure_ShouldDropLiveConversations()
    {
        var rig = new Rig();
        await using var host = rig.NewHost();
        await host.GenerateAsync(Req(LlmPriority.Interactive, "q1", key: "chat"));
        Assert.Equal(1, host.LiveConversationCount);

        await host.ReconfigureAsync(new LlmHostSettings("big.litertlm", "cpu", 8192));

        Assert.Equal(0, host.LiveConversationCount);
        Assert.True(rig.Engines[0].Conversations[0].Disposed);
    }

    [Fact]
    public async Task Reconfigure_WhileBackgroundRuns_ShouldRerunTheUnitOnTheNewEngine()
    {
        var rig = new Rig();
        FakeReply? held = null;
        var calls = 0;
        rig.NewEngine = () => new FakeGemmaEngine
        {
            ReplyFor = call => Interlocked.Increment(ref calls) == 1
                ? held = FakeReply.Held(1, "a", "b", "c")
                : FakeReply.Text("a", "b", "c"),
        };
        await using var host = rig.NewHost();

        var background = host.GenerateAsync(Req(LlmPriority.Background, "bg"));
        await WaitUntil(() => held?.Reached.Task.IsCompleted == true);

        await host.ReconfigureAsync(new LlmHostSettings("big.litertlm", "cpu", 8192));

        Assert.Equal("abc", await background); // whole text, no half-streamed duplicate
        Assert.Equal(2, rig.Engines.Count);
        Assert.True(rig.Engines[0].Disposed);
        Assert.Equal("big.litertlm", rig.LoadedWith[1].ModelPath);
        Assert.All(rig.Engines, e => Assert.Equal(1, e.MaxInFlight));
    }

    [Fact]
    public async Task Reconfigure_WhileInteractiveAnswers_ShouldContinueWithoutDuplicates()
    {
        var rig = new Rig();
        FakeReply? held = null;
        rig.NewEngine = () => new FakeGemmaEngine
        {
            ReplyFor = call => call.UserText == new GemmaEngineHostOptions { ModelPath = "x" }.ContinuationPrompt
                ? FakeReply.Text("wor", "ld")
                : held = FakeReply.Held(2, "he", "llo ", "wor", "ld"),
        };
        await using var host = rig.NewHost();

        var received = new List<string>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var c in host.GenerateStreamAsync(Req(LlmPriority.Interactive, "question", key: "chat")))
                lock (received)
                    received.Add(c);
        });
        await WaitUntil(() => held?.Reached.Task.IsCompleted == true);

        await host.ReconfigureAsync(new LlmHostSettings("big.litertlm", "cpu", 8192));
        await consumer;

        Assert.Equal("hello world", string.Concat(received));
        // A snapshot cannot cross engines: the new engine got a conversation rebuilt from the text so far.
        var rebuilt = Assert.Single(rig.Engines[1].Conversations);
        Assert.Empty(rig.Engines[1].Resumed);
        Assert.Contains(rebuilt.Spec.History, m => m.Role == LlmRole.Assistant && m.Text == "hello ");
    }

    [Fact]
    public async Task Reconfigure_WhileLiveHasStreamedText_ShouldFailThatRequestAndKeepHostUsable()
    {
        var rig = new Rig();
        FakeReply? held = null;
        var calls = 0;
        rig.NewEngine = () => new FakeGemmaEngine
        {
            ReplyFor = call => Interlocked.Increment(ref calls) == 1
                ? held = FakeReply.Held(1, "a", "b")
                : FakeReply.Text("fresh"),
        };
        await using var host = rig.NewHost();

        var live = host.GenerateAsync(Req(LlmPriority.Live, "sentence"));
        await WaitUntil(() => held?.Reached.Task.IsCompleted == true);
        await host.ReconfigureAsync(new LlmHostSettings("big.litertlm", "cpu", 8192));

        await Assert.ThrowsAsync<LlmUnavailableException>(() => live);
        Assert.Equal("fresh", await host.GenerateAsync(Req(LlmPriority.Live, "next")));
    }

    [Fact]
    public async Task Reconfigure_ToMissingModel_ShouldReportUnavailableUntilSwitchedBack()
    {
        var rig = new Rig();
        rig.MissingPaths.Add("gone.litertlm");
        await using var host = rig.NewHost();

        await host.ReconfigureAsync(new LlmHostSettings("gone.litertlm", "cpu", 4096));
        Assert.False(host.IsModelInstalled);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => host.GenerateAsync(Req(LlmPriority.Interactive, "x")));

        await host.ReconfigureAsync(new LlmHostSettings("small.litertlm", "cpu", 4096));
        Assert.True(host.IsModelInstalled);
        Assert.Equal("R(y)", await host.GenerateAsync(Req(LlmPriority.Interactive, "y")));
    }

    [Fact]
    public async Task GpuBackendThatFailsToLoad_ShouldFallBackToCpu()
    {
        var attempts = new List<string>();
        await using var host = new GemmaEngineHost(
            new GemmaEngineHostOptions { ModelPath = "m", Backend = "gpu", IdleUnloadAfter = null },
            options =>
            {
                lock (attempts)
                    attempts.Add(options.Backend);
                return options.Backend == "gpu" ? throw new InvalidOperationException("no vulkan") : new FakeGemmaEngine();
            },
            _ => true);

        Assert.Equal("R(x)", await host.GenerateAsync(Req(LlmPriority.Interactive, "x")));

        Assert.Equal(["gpu", "cpu"], attempts);
        Assert.Contains("no vulkan", host.BackendFallbackReason);
        Assert.Equal("gpu", host.Settings.Backend); // the user's choice stays visible
    }

    [Fact]
    public async Task SharedHost_ShouldForwardReconfigure()
    {
        var rig = new Rig();
        await using var real = rig.NewHost();
        var shared = new SharedLlmHost(real);

        await shared.ReconfigureAsync(new LlmHostSettings("big.litertlm", "cpu", 8192));

        Assert.Equal(8192, shared.ContextTokens);
        Assert.Equal("big.litertlm", shared.Settings.ModelPath);
    }

    [Fact]
    public async Task Reconfigure_ShouldRejectBadSettings()
    {
        var rig = new Rig();
        await using var host = rig.NewHost();

        await Assert.ThrowsAsync<ArgumentException>(() => host.ReconfigureAsync(new LlmHostSettings("", "cpu", 4096)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.ReconfigureAsync(new LlmHostSettings("x", "cpu", 10)));
    }
}
