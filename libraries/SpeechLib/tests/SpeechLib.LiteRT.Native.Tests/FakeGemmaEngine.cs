using System.Runtime.CompilerServices;
using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native.Tests;

/// <summary>What one fake send produces.</summary>
internal sealed class FakeReply
{
    public string[] Chunks { get; init; } = ["ok"];

    /// <summary>After this many chunks the send parks until <see cref="Release"/> completes (-1 = never).</summary>
    public int HoldAfter { get; init; } = -1;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Throws instead of streaming (checked at the start of the send).</summary>
    public Func<Exception>? FailWith { get; init; }

    public static FakeReply Text(params string[] chunks) => new() { Chunks = chunks };

    public static FakeReply Held(int holdAfter, params string[] chunks) => new() { Chunks = chunks, HoldAfter = holdAfter };
}

internal sealed record FakeCall(FakeConversation Conversation, string UserText, int SendIndex);

/// <summary>An in-memory <see cref="IGemmaEngine"/> that also verifies the lease: no two native operations overlap.</summary>
internal sealed class FakeGemmaEngine : IGemmaEngine
{
    private int _inFlight;

    public int MaxNumTokens => 4096;
    public int MaxInFlight { get; private set; }
    public bool Disposed { get; private set; }
    public List<FakeConversation> Conversations { get; } = [];
    public List<FakeConversation> Resumed { get; } = [];
    public TimeSpan ChunkDelay { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>Picks the reply for a send. Default: one chunk "R(text)".</summary>
    public Func<FakeCall, FakeReply> ReplyFor { get; set; } = call => FakeReply.Text($"R({call.UserText})");

    public int CountTokens(string text) => text.Length / 4 + 1;

    public IGemmaConversation CreateConversation(GemmaConversationSpec spec)
    {
        using var _ = Operation();
        var conversation = new FakeConversation(this, spec, [.. spec.History]);
        lock (Conversations)
            Conversations.Add(conversation);
        return conversation;
    }

    public IGemmaConversation Resume(IGemmaSnapshot snapshot)
    {
        using var _ = Operation();
        var s = (FakeSnapshot)snapshot;
        if (!ReferenceEquals(s.Owner, this))
            throw new ObjectDisposedException("engine", "The snapshot belongs to another engine.");
        var turns = new List<LlmMessage>(s.Turns);
        if (!string.IsNullOrEmpty(s.Partial))
            turns.Add(new LlmMessage(LlmRole.Assistant, s.Partial));
        var conversation = new FakeConversation(this, s.Spec, turns) { IsResumed = true, ResumedPartial = s.Partial };
        lock (Conversations)
        {
            Conversations.Add(conversation);
            Resumed.Add(conversation);
        }

        return conversation;
    }

    public void Dispose() => Disposed = true;

    internal IDisposable Operation()
    {
        var now = Interlocked.Increment(ref _inFlight);
        lock (this)
            MaxInFlight = Math.Max(MaxInFlight, now);
        return new Exit(this);
    }

    private sealed class Exit(FakeGemmaEngine engine) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref engine._inFlight);
    }
}

internal sealed record FakeSnapshot(GemmaConversationSpec Spec, IReadOnlyList<LlmMessage> Turns, string? Partial, FakeGemmaEngine Owner) : IGemmaSnapshot;

internal sealed class FakeConversation(FakeGemmaEngine engine, GemmaConversationSpec spec, List<LlmMessage> turns) : IGemmaConversation
{
    public GemmaConversationSpec Spec { get; } = spec;
    public List<LlmMessage> Turns { get; } = turns;
    public List<string> Sends { get; } = [];
    public List<GemmaSendSpec> SendSpecs { get; } = [];
    public bool Disposed { get; private set; }
    public bool Dead { get; private set; }
    public bool IsResumed { get; init; }
    public string? ResumedPartial { get; init; }
    public bool FullFlag { get; set; }

    public int TokenCountCached => Turns.Sum(t => t.Text.Length) / 4;
    public bool IsContextFull => FullFlag;

    public IGemmaSnapshot Snapshot(string? partialResponse) => new FakeSnapshot(Spec, [.. Turns], partialResponse, engine);

    public async IAsyncEnumerable<string> SendStreamingAsync(
        string userText, GemmaSendSpec send, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var op = engine.Operation();
        using var registration = cancellationToken.Register(() => Dead = true);

        if (Dead || Disposed)
            throw new InvalidOperationException("CANCELLED: conversation is dead");

        int index;
        lock (Sends)
        {
            index = Sends.Count;
            Sends.Add(userText);
            SendSpecs.Add(send);
        }

        Turns.Add(new LlmMessage(LlmRole.User, userText));
        var reply = engine.ReplyFor(new FakeCall(this, userText, index));
        if (reply.FailWith is { } fail)
            throw fail();

        var text = new System.Text.StringBuilder();
        for (var i = 0; i < reply.Chunks.Length; i++)
        {
            if (i == reply.HoldAfter)
            {
                reply.Reached.TrySetResult();
                await reply.Release.Task.WaitAsync(cancellationToken);
            }

            await Task.Delay(engine.ChunkDelay, cancellationToken);
            text.Append(reply.Chunks[i]);
            yield return reply.Chunks[i];
        }

        if (reply.HoldAfter >= reply.Chunks.Length)
        {
            reply.Reached.TrySetResult();
            await reply.Release.Task.WaitAsync(cancellationToken);
        }

        Turns.Add(new LlmMessage(LlmRole.Assistant, text.ToString()));
    }

    public void Dispose() => Disposed = true;
}
