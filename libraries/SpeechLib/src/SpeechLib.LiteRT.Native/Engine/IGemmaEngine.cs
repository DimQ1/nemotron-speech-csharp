using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native;

// A small abstraction over the LiteRtLmSharp types so GemmaEngineHost's scheduling logic can be unit-tested with a
// fake engine and without the 2.4 GB model. Everything here is called by the host's single worker loop only (the
// "lease"), except IGemmaEngine.CountTokens, which the native tokenizer allows while a generation runs.

/// <summary>Sampler requested explicitly (top-p). Null in a spec = the model file's own sampler.</summary>
internal sealed record GemmaSamplerSpec(double Temperature, double TopP);

/// <param name="System">System prompt or null.</param>
/// <param name="History">Completed turns (user/assistant) the conversation starts with.</param>
/// <param name="MaxOutputTokens">Cap on one reply.</param>
/// <param name="Sampler">Explicit sampler or null.</param>
internal sealed record GemmaConversationSpec(
    string? System,
    IReadOnlyList<LlmMessage> History,
    int MaxOutputTokens,
    GemmaSamplerSpec? Sampler);

/// <param name="NoRepeatNgram">Bans repeating an n-gram of this many tokens inside the reply. 0 = off.</param>
internal sealed record GemmaSendSpec(int NoRepeatNgram);

/// <summary>An opaque capture of a cancelled conversation, enough to continue it on a fresh one.</summary>
internal interface IGemmaSnapshot;

/// <summary>Thrown when the backend does not implement the requested sampler.</summary>
internal sealed class GemmaSamplerException(string message, Exception? inner = null) : Exception(message, inner);

internal interface IGemmaEngine : IDisposable
{
    int MaxNumTokens { get; }

    /// <summary>Tokenizer; does not block while a generation runs.</summary>
    int CountTokens(string text);

    /// <summary>Blocks while another generation runs, so call it under the lease.</summary>
    IGemmaConversation CreateConversation(GemmaConversationSpec spec);

    /// <summary>A fresh conversation holding the snapshot's turns plus its partial reply as a model turn.</summary>
    IGemmaConversation Resume(IGemmaSnapshot snapshot);
}

internal interface IGemmaConversation : IDisposable
{
    /// <summary>
    /// Sends one user turn and streams the answer text. Cancelling <paramref name="cancellationToken"/> stops the
    /// decode (about 40 ms) and throws <see cref="OperationCanceledException"/>; the conversation is dead afterwards.
    /// Throws <see cref="GemmaSamplerException"/> for an unsupported sampler and
    /// <see cref="LlmContextOverflowException"/> when the context is full.
    /// </summary>
    IAsyncEnumerable<string> SendStreamingAsync(string userText, GemmaSendSpec send, CancellationToken cancellationToken);

    /// <summary>Non-blocking token count as of the last completed send.</summary>
    int TokenCountCached { get; }

    bool IsContextFull { get; }

    /// <summary>Captures the turns plus <paramref name="partialResponse"/> for <see cref="IGemmaEngine.Resume"/>.</summary>
    IGemmaSnapshot Snapshot(string? partialResponse);
}
