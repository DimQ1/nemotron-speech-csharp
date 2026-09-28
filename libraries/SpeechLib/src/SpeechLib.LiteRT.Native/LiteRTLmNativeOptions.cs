using LiteRtLmSharp;
using SpeechLib.Translation;

namespace SpeechLib.LiteRT.Native;

/// <summary>
/// Configuration for the in-process LiteRT-LM translator: it loads a Gemma 4
/// model in <c>.litertlm</c> format directly (via LiteRtLmSharp 1.2, native
/// LiteRT-LM v0.16), without an HTTP server.
/// </summary>
public sealed class LiteRTLmNativeOptions
{
    /// <summary>Path to the <c>.litertlm</c> model file.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Compute backend: <c>"cpu"</c> (default) or <c>"gpu"</c>.</summary>
    public string Backend { get; init; } = "cpu";

    /// <summary>
    /// Number of CPU threads. 0 (default) lets the LiteRT runtime pick its own
    /// default (all available cores).
    /// </summary>
    public int NumThreads { get; init; }

    /// <summary>Maximum tokens to generate for a translation.</summary>
    public int MaxTokens { get; init; } = 256;

    /// <summary>
    /// Model context window (max tokens). The translations are short ASR
    /// sentences (a few hundred chars at most), so a large window is mostly
    /// padding that only costs memory and prefill time on CPU. Kept modest at
    /// 2048 so the constant per-call overhead stays low.
    /// </summary>
    public int MaxContextTokens { get; init; } = 2048;

    /// <summary>
    /// Greedy (argmax) decoding. Translation wants the single most likely rendering;
    /// the model file's own sampler (top-k 40 / top-p 0.95 / temperature 1.0) makes
    /// every draft pass come out slightly different and roughly doubles decode time.
    /// </summary>
    public bool Greedy { get; init; } = true;

    /// <summary>
    /// Bans the decoder from repeating an n-gram of this many tokens it already
    /// produced in this reply, stopping "and the and the and the" loops. 0 = off.
    /// Applies to the reply only, so names that recur in source and translation are
    /// unaffected.
    /// </summary>
    public int NoRepeatNgramSize { get; init; } = 8;

    /// <summary>
    /// Model weight-cache location passed to the engine. This controls the
    /// XNNPack weight cache (persisted weights so a repeated model load is
    /// faster), not the per-call prompt prefix. Point <see cref="LiteRtCache.Directory"/>
    /// at a writable folder if you want a disk-backed weight cache.
    /// </summary>
    public LiteRtCache Cache { get; init; } = LiteRtCache.Default;

    /// <summary>
    /// Native log verbosity. Defaults to <c>Warning</c> so model-load progress is
    /// not drowned out; set to <c>Silent</c> to suppress everything.
    /// </summary>
    public LiteRTLmLogLevel LogLevel { get; init; } = LiteRTLmLogLevel.Warning;

    /// <summary>
    /// Optional extra system-prompt text appended to the built-in translation
    /// instruction (terminology, style, casing, etc.). Empty by default.
    /// </summary>
    public string AdditionalSystemPrompt { get; init; } = "";

    /// <summary>Builds the system prompt (see <see cref="TranslationPrompt"/>).</summary>
    public string BuildSystemPrompt(string targetLang, string? sourceLang) =>
        TranslationPrompt.BuildSystemPrompt(targetLang, sourceLang, AdditionalSystemPrompt);
}

/// <summary>Log severity exposed on the managed side.</summary>
public enum LiteRTLmLogLevel
{
    Verbose = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5,
    Silent = 1000,
}
