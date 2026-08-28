using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Decorators;
using SpeechLib.ParakeetTdt;

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

        IStreamingSpeechRecognizer recognizer = isParakeet
            ? new ParakeetTdtRecognizer(options.ModelPath, executionProvider: options.ExecutionProvider)
            : new ModelSession(
                options.ModelPath,
                options.ExecutionProvider,
                langId,
                options.UseVad,
                new GeneratorParamsArgs
                {
                    do_sample = false,
                    repetition_penalty = options.RepetitionPenalty
                });

        // Universal Silero VAD gate: recognizers without native VAD (GenAI VAD)
        // or utterance endpointing get the shared external gate.
        if (recognizer is not IUtteranceStreamingRecognizer &&
            recognizer is not IRuntimeConfigurable)
            recognizer = WrapWithVad(recognizer, options.UseVad, options.SileroVadPath);

        if (!isParakeet)
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

    /// <summary>Enable voice activity detection where supported.</summary>
    public bool UseVad { get; init; }

    /// <summary>Repetition penalty (Nemotron decoder).</summary>
    public double RepetitionPenalty { get; init; } = 1.1;

    /// <summary>Path to the shared Silero VAD model, used when the provider has no native VAD.</summary>
    public string? SileroVadPath { get; init; }
}
