using LiteRtLmSharp;

namespace SpeechLib.LiteRT.Native;

/// <summary>Settings of the process-wide Gemma engine owned by <see cref="GemmaEngineHost"/>.</summary>
/// <remarks>Engine settings are global: the engine is created once with these and every feature shares it.</remarks>
public sealed class GemmaEngineHostOptions
{
    /// <summary>Path to the <c>.litertlm</c> model file. The host loads it lazily at the first request.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Compute backend: <c>"cpu"</c> (default) or <c>"gpu"</c>.</summary>
    public string Backend { get; init; } = "cpu";

    /// <summary>CPU threads; 0 lets the runtime use all cores.</summary>
    public int NumThreads { get; init; }

    /// <summary>
    /// Context window (prompt + answer). 4096 is the ceiling the app plans for: about 3.2 GB resident, 8192 would be
    /// about 4.9 GB. Every live conversation adds about 37 MB.
    /// </summary>
    public int MaxNumTokens { get; init; } = 4096;

    /// <summary>Weight-cache location handed to the engine.</summary>
    public LiteRtCache Cache { get; init; } = LiteRtCache.Default;

    /// <summary>Native log verbosity.</summary>
    public LiteRTLmLogLevel LogLevel { get; init; } = LiteRTLmLogLevel.Warning;

    /// <summary>
    /// The engine (about 3 GB) is unloaded when nothing was requested for this long, and reloads (2.4-2.9 s with a
    /// warm file cache, about 6 s cold) at the next request. Null or zero keeps it loaded until the host is disposed (and then
    /// <see cref="BackgroundIdleUnloadAfter"/> does not apply either). Applies after live translation and chat; after
    /// background work the shorter <see cref="BackgroundIdleUnloadAfter"/> does.
    /// </summary>
    public TimeSpan? IdleUnloadAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the engine stays loaded after the last request was <see cref="SpeechLib.Llm.LlmPriority.Background"/> work (the
    /// title and summary of a saved recording): nobody is waiting for the model then, so it is released soon, but not at
    /// once: the next job of a queue that is being worked off follows within seconds. Never longer than
    /// <see cref="IdleUnloadAfter"/>; null or zero uses that value.
    /// </summary>
    public TimeSpan? BackgroundIdleUnloadAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Live stateful chat conversations kept on the engine (least recently used are dropped).</summary>
    public int MaxLiveConversations { get; init; } = 3;

    /// <summary>Reply cap when a request does not set <see cref="SpeechLib.Llm.LlmSampling.MaxOutputTokens"/>.</summary>
    public int DefaultMaxOutputTokens { get; init; } = 1024;

    /// <summary>
    /// No-repeat n-gram size applied to <see cref="SpeechLib.Llm.LlmPriority.Live"/> replies (live translation), which
    /// stops "and the and the" decode loops. 0 = off. Chat and background work never use it, so quoting is unaffected.
    /// </summary>
    public int LiveNoRepeatNgramSize { get; init; } = 8;

    /// <summary>
    /// Receives lifecycle diagnostics: who disposed the host (with the stack), and a worker failure. Called on any
    /// thread; exceptions are swallowed.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>The user turn sent to continue an interactive answer that was preempted mid-way.</summary>
    public string ContinuationPrompt { get; init; } =
        "Continue your previous answer exactly where it stopped. Do not repeat anything you already wrote and do not add an introduction.";

    /// <summary>A copy with the runtime-switchable values replaced (keep in step with the properties above).</summary>
    internal GemmaEngineHostOptions With(string modelPath, string backend, int maxNumTokens) => new()
    {
        ModelPath = modelPath,
        Backend = backend,
        NumThreads = NumThreads,
        MaxNumTokens = maxNumTokens,
        Cache = Cache,
        LogLevel = LogLevel,
        IdleUnloadAfter = IdleUnloadAfter,
        BackgroundIdleUnloadAfter = BackgroundIdleUnloadAfter,
        MaxLiveConversations = MaxLiveConversations,
        DefaultMaxOutputTokens = DefaultMaxOutputTokens,
        LiveNoRepeatNgramSize = LiveNoRepeatNgramSize,
        Diagnostics = Diagnostics,
        ContinuationPrompt = ContinuationPrompt,
    };
}
