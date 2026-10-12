using System.Runtime.CompilerServices;
using SpeechLib.Llm;
using SpeechLib.Translation;

namespace SpeechLib.LiteRT.Native.Tests;

public sealed class Unit_LiteRTLmNativeTranslatorTests
{
    private sealed class RecordingHost : ILlmHost
    {
        public List<LlmRequest> Requests { get; } = [];
        public Func<LlmRequest, CancellationToken, IAsyncEnumerable<string>> Handler { get; set; } =
            (_, _) => Chunks("Привет", ", мир");

        public bool IsModelInstalled => true;
        public int ContextTokens => 4096;
        public LlmHostStatus Status => new(LlmHostState.Idle, null, 0);
        public event Action<LlmHostStatus>? StatusChanged { add { } remove { } }

        public IAsyncEnumerable<string> GenerateStreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests)
                Requests.Add(request);
            return Handler(request, cancellationToken);
        }

        public async Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var text = new System.Text.StringBuilder();
            await foreach (var c in GenerateStreamAsync(request, cancellationToken))
                text.Append(c);
            return text.ToString();
        }

        public int CountTokens(string text) => text.Length;
        public void ReleaseConversation(string conversationKey) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public static async IAsyncEnumerable<string> Chunks(params string[] chunks)
        {
            foreach (var c in chunks)
            {
                await Task.Yield();
                yield return c;
            }
        }
    }

    [Fact]
    public async Task Translate_ShouldSendLivePriorityRequestWithSystemPromptAndPreviousPair()
    {
        var host = new RecordingHost();
        using var translator = new LiteRTLmNativeTranslator(host, new LiteRTLmNativeOptions { MaxTokens = 123 });

        var result = await translator.TranslateAsync(new TranslationRequest("Hello, world.", "Russian")
        {
            PreviousSource = "Good morning.",
            PreviousTranslation = "Доброе утро.",
        });

        Assert.Equal("Привет, мир", result);
        var request = Assert.Single(host.Requests);
        Assert.Equal(LlmPriority.Live, request.Priority);
        Assert.Null(request.ConversationKey);
        Assert.Equal(123, request.Sampling?.MaxOutputTokens);
        Assert.Null(request.Sampling?.Temperature);
        Assert.Equal(
            [LlmRole.System, LlmRole.User, LlmRole.Assistant, LlmRole.User],
            request.Messages.Select(m => m.Role));
        Assert.Contains("Russian", request.Messages[0].Text);
        Assert.Equal("Good morning.", request.Messages[1].Text);
        Assert.Equal("Доброе утро.", request.Messages[2].Text);
        Assert.Equal("Hello, world.", request.Messages[3].Text);
    }

    [Fact]
    public async Task Translate_ShouldOmitHistoryWithoutContextAndSkipBlankText()
    {
        var host = new RecordingHost();
        using var translator = new LiteRTLmNativeTranslator(host, new LiteRTLmNativeOptions());

        Assert.Null(await translator.TranslateAsync("   ", "Russian"));
        await translator.TranslateAsync("Hello", "Russian", "English");

        var request = Assert.Single(host.Requests);
        Assert.Equal([LlmRole.System, LlmRole.User], request.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task TranslateStream_ShouldCancelOlderQueuedRevisionOfTheSameSentence()
    {
        var host = new RecordingHost
        {
            Handler = (request, ct) => request.Messages[^1].Text == "hello"
                ? NeverProducing(ct)
                : RecordingHost.Chunks("ok"),
        };
        using var translator = new LiteRTLmNativeTranslator(host, new LiteRTLmNativeOptions());

        var older = translator.TranslateAsync(new TranslationRequest("hello", "Russian"));
        await Task.Delay(50);
        var newer = await translator.TranslateAsync(new TranslationRequest("hello world", "Russian"));

        Assert.Equal("ok", newer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older);
    }

    [Fact]
    public async Task TranslateStream_ShouldNotCancelUnrelatedOlderRequest()
    {
        var gate = new TaskCompletionSource();
        var host = new RecordingHost
        {
            Handler = (request, ct) => request.Messages[^1].Text == "first" ? Delayed(gate.Task, "one") : RecordingHost.Chunks("two"),
        };
        using var translator = new LiteRTLmNativeTranslator(host, new LiteRTLmNativeOptions());

        var first = translator.TranslateAsync(new TranslationRequest("first", "Russian"));
        await Task.Delay(50);
        var second = await translator.TranslateAsync(new TranslationRequest("second sentence", "Russian"));
        gate.SetResult();

        Assert.Equal("two", second);
        Assert.Equal("one", await first);
    }

    [Fact]
    public async Task Dispose_ShouldCancelInFlightRequestsButNotTheSharedHost()
    {
        var host = new RecordingHost { Handler = (_, ct) => NeverProducing(ct) };
        var translator = new LiteRTLmNativeTranslator(host, new LiteRTLmNativeOptions());

        var pending = translator.TranslateAsync(new TranslationRequest("hello", "Russian"));
        await Task.Delay(50);
        translator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static async IAsyncEnumerable<string> NeverProducing([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    private static async IAsyncEnumerable<string> Delayed(Task gate, string text)
    {
        await gate;
        yield return text;
    }
}
