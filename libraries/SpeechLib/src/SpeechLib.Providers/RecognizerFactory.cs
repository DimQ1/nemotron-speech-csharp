using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.ModelFormats;
using SpeechLib.Nemotron;
using SpeechLib.ParakeetTdt;
using SpeechLib.Qwen3;
using SpeechLib.VibeVoice;

namespace SpeechLib.Providers;

/// <summary>
/// Builds a streaming recognizer for a model folder, hiding the provider choice
/// (Nemotron, Parakeet TDT, Qwen3, VibeVoice) and the shared Silero VAD gate behind a
/// single entry point. Applications work only with the returned
/// <see cref="IStreamingSpeechRecognizer"/>.
/// </summary>
public static class RecognizerFactory
{
    public static IStreamingSpeechRecognizer Create(RecognizerFactoryOptions options)
    {
        var langId = LanguageMapper.Resolve(options.Language);
        bool isParakeet = ParakeetTdtRecognizer.IsParakeetTdtModel(options.ModelPath);
        bool isVibeVoice = VibeVoiceModelDetector.IsVibeVoiceAsrModel(options.ModelPath);
        bool isQwen3Streaming = Qwen3AsrStreamingRecognizer.IsQwen3AsrStreamingModel(options.ModelPath);
        bool isQwen3 = !isQwen3Streaming && Qwen3AsrRecognizer.IsQwen3AsrModel(options.ModelPath);

        // The WebGPU EP hard-crashes the process (0xC0000409, fail-fast in native
        // code) on the Qwen3 graphs, verified for both the streaming and the plain
        // model. A persisted setting must never take the app down, so the request
        // is downgraded to CPU here, where the model family is known.
        var executionProvider = options.ExecutionProvider;
        if (WebGpuRequest.IsWebGpuRequest(executionProvider) && (isQwen3 || isQwen3Streaming))
        {
            Console.WriteLine($"  Warning: WebGPU is not supported by the Qwen3 graphs (crash); using CPU for {options.ModelPath}.");
            executionProvider = "cpu";
        }

        IStreamingSpeechRecognizer recognizer = isVibeVoice
            ? new VibeVoiceAsrRecognizer(
                options.ModelPath,
                executionProvider: executionProvider)
            : isQwen3Streaming
            ? new Qwen3AsrStreamingRecognizer(
                options.ModelPath,
                executionProvider: executionProvider,
                language: options.Language)
            : isQwen3
            ? new Qwen3AsrRecognizer(
                options.ModelPath,
                executionProvider: executionProvider,
                language: options.Language)
            : isParakeet
            ? new ParakeetTdtRecognizer(
                options.ModelPath,
                chunkSeconds: options.StreamingChunkSeconds ?? ParakeetTdtRecognizer.DefaultChunkSeconds,
                leftContextSeconds: options.StreamingLeftContextSeconds ?? ParakeetTdtRecognizer.DefaultLeftContextSeconds,
                rightContextSeconds: options.StreamingRightContextSeconds ?? ParakeetTdtRecognizer.DefaultRightContextSeconds,
                executionProvider: executionProvider,
                previewPartials: options.StreamingPreview,
                silenceContextSeconds: options.StreamingSilenceContextSeconds ?? ParakeetTdtRecognizer.DefaultSilenceContextSeconds)
            : CreateNemotron(options, langId, executionProvider);

        if (recognizer is ITranslationConfigurable translationConfigurable)
            translationConfigurable.TrySetTranslation(options.TranslationEnabled, options.TranslationLanguage ?? "auto");

        // Universal Silero VAD gate: recognizers without native VAD
        // or utterance endpointing get the shared external gate.
        if (recognizer is not IUtteranceStreamingRecognizer &&
            recognizer is not IRuntimeConfigurable)
            recognizer = WrapWithVad(recognizer, options.UseVad, options.SileroVadPath);

        return recognizer;
    }

    private static IStreamingSpeechRecognizer CreateNemotron(RecognizerFactoryOptions options, string? langId, string executionProvider)
    {
        // The model has no input normalization and returns nothing for quiet microphones (silent at a
        // -40 dBFS peak), so its input goes through automatic gain control. The gain sits inside the
        // recognizer rather than in a decorator: a decorator would hide the utterance and word-timing
        // interfaces from the apps.
        return new NemotronUtteranceRecognizer(
            options.ModelPath,
            executionProvider,
            langId,
            options.UseVad,
            new NemotronRecognizerOptions
            {
                PauseState = options.NemotronPauseState ?? NemotronPauseStateOptions.Parity,
                FlushClosingChunks = options.NemotronFlushClosingChunks,
            },
            new NemotronUtteranceOptions { AutoGain = true, KeepLanguageTagInFinal = options.KeepLanguageTagInFinal });
    }

    private static IStreamingSpeechRecognizer WrapWithVad(
        IStreamingSpeechRecognizer inner, bool useVad, string? vadPath)
    {
        if (string.IsNullOrEmpty(vadPath) || !File.Exists(vadPath))
            return inner;

        try
        {
            var vad = new SileroVadFilter(vadPath);
            var wrapped = new VadSpeechRecognizer(inner, vad);
            wrapped.TrySetVad(useVad);
            return wrapped;
        }
        catch
        {
            return inner;
        }
    }
}

/// <summary>Parameters for <see cref="RecognizerFactory.Create"/>.</summary>
public sealed record RecognizerFactoryOptions
{
    /// <summary>Path to the model folder (genai_config.json or config.json).</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>
    /// Execution provider name: <c>cpu</c> (default) or <c>webgpu[:hp|:lp]</c>; stale <c>cuda</c>/<c>dml</c> values run on the CPU. Nemotron runs
    /// only its encoder on WebGPU, on a discrete graphics card, and falls back to the CPU otherwise (see
    /// <see cref="SpeechLib.Nemotron.NemotronProviderInfo"/>); Qwen3 always runs on the CPU.
    /// </summary>
    public string ExecutionProvider { get; init; } = "cpu";

    /// <summary>BCP-47 language code or numeric lang_id; null = auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>Enable prompt-based translation when the selected recognizer supports it.</summary>
    public bool TranslationEnabled { get; init; }

    /// <summary>BCP-47 target language for prompt-based translation.</summary>
    public string? TranslationLanguage { get; init; }

    /// <summary>Enable voice activity detection where supported.</summary>
    public bool UseVad { get; init; }

    /// <summary>
    /// Kept for settings compatibility; no recognizer reads it (the Nemotron transducer has no repetition penalty).
    /// </summary>
    public double RepetitionPenalty { get; init; } = 1.1;

    /// <summary>
    /// Nemotron: keep the closing language tag (<c>&lt;en-US&gt;</c>) in the text of a finished utterance, where History reads the
    /// detected language from. False for a caller that types the growing text into another application: the text of an open
    /// utterance never has the tag, so only a tag-free final text extends what was typed (see <c>IncrementalTextTyper</c>).
    /// </summary>
    public bool KeepLanguageTagInFinal { get; init; } = true;

    /// <summary>
    /// Nemotron: what the model state does when speech resumes after a pause (<see cref="NemotronRecognizerOptions.PauseState"/>);
    /// null keeps it through the pause, as GenAI did.
    /// </summary>
    public NemotronPauseStateOptions? NemotronPauseState { get; init; }

    /// <summary>
    /// Nemotron: chunks of zeros encoded after the padded remainder when the stream is flushed (Stop, the end of a re-recognized
    /// file), so that the last word of a stream that ends right after it is not lost (<see cref="NemotronRecognizerOptions.FlushClosingChunks"/>);
    /// 0 is GenAI's single padded chunk.
    /// </summary>
    public int NemotronFlushClosingChunks { get; init; }

    /// <summary>Path to the shared Silero VAD model, used when the provider has no native VAD.</summary>
    public string? SileroVadPath { get; init; }

    /// <summary>
    /// Buffer-streaming step in seconds (Parakeet TDT; other providers ignore it).
    /// Null = provider default (0.32 s). Smaller = text appears sooner, more encoder
    /// passes per second; the recognizer lengthens the step by itself when a step takes
    /// longer to compute than the audio it covers.
    /// </summary>
    public double? StreamingChunkSeconds { get; init; }

    /// <summary>
    /// Future audio a frame needs before it is committed, in seconds (Parakeet TDT).
    /// Null = provider default (1.0 s). Larger = more accurate commits, higher latency.
    /// </summary>
    public double? StreamingRightContextSeconds { get; init; }

    /// <summary>
    /// Audio prepended to every encoder window, in seconds (Parakeet TDT).
    /// Null = provider default (5.0 s). This is the main CPU knob: each step encodes
    /// left context + uncommitted audio.
    /// </summary>
    public double? StreamingLeftContextSeconds { get; init; }

    /// <summary>
    /// Decode the uncommitted tail into revisable partial text on every step
    /// (Parakeet TDT). Off = only committed text is reported.
    /// </summary>
    public bool StreamingPreview { get; init; } = true;

    /// <summary>
    /// Future audio a token-free stretch needs before the committed boundary moves past
    /// it, in seconds (Parakeet TDT). Null = provider default (2.0 s). Bounds the encoder
    /// window during pauses; sentence onsets after a pause are re-decoded with up to this
    /// much right context.
    /// </summary>
    public double? StreamingSilenceContextSeconds { get; init; }
}
