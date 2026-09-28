using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Decorators;
using SpeechLib.ModelDownload;
using SpeechLib.ParakeetTdt;
using SpeechLib.Qwen3;
using SpeechLib.VibeVoice;

namespace SpeechLib.Providers;

/// <summary>
/// Builds a streaming recognizer for a model folder, hiding the provider choice
/// (Nemotron GenAI vs Parakeet TDT ONNX) and the shared decorator wiring (Silero
/// VAD gate, metrics) behind a single entry point. Applications work only with
/// the returned <see cref="IStreamingSpeechRecognizer"/>.
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
            : new ModelSession(
                options.ModelPath,
                executionProvider,
                langId,
                options.UseVad,
                new GeneratorParamsArgs
                {
                    do_sample = false,
                    repetition_penalty = options.RepetitionPenalty
                });

        if (recognizer is ITranslationConfigurable translationConfigurable)
            translationConfigurable.TrySetTranslation(options.TranslationEnabled, options.TranslationLanguage ?? "auto");

        // Universal Silero VAD gate: recognizers without native VAD (GenAI VAD)
        // or utterance endpointing get the shared external gate.
        if (recognizer is not IUtteranceStreamingRecognizer &&
            recognizer is not IRuntimeConfigurable)
            recognizer = WrapWithVad(recognizer, options.UseVad, options.SileroVadPath);

        if (!isParakeet && !isVibeVoice && !isQwen3Streaming && !isQwen3)
            recognizer = new MetricsRecognizerDecorator(recognizer, "ModelSession");

        return recognizer;
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

    /// <summary>Execution provider name (cpu/cuda/dml), provider-specific.</summary>
    public string ExecutionProvider { get; init; } = "cpu";

    /// <summary>BCP-47 language code or numeric lang_id; null = auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>Enable prompt-based translation when the selected recognizer supports it.</summary>
    public bool TranslationEnabled { get; init; }

    /// <summary>BCP-47 target language for prompt-based translation.</summary>
    public string? TranslationLanguage { get; init; }

    /// <summary>Enable voice activity detection where supported.</summary>
    public bool UseVad { get; init; }

    /// <summary>Repetition penalty (Nemotron decoder).</summary>
    public double RepetitionPenalty { get; init; } = 1.1;

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
