using System.Text.Json;

namespace SpeechLib.ModelDownload;

/// <summary>Detects the VibeVoice streaming ONNX package produced by the exporter.</summary>
public static class VibeVoiceModelDetector
{
    public const string ModelType = "vibevoice_asr_onnx_streaming";

    private static readonly string[] RequiredFiles =
    [
        "vibevoice_onnx_config.json",
        "speech_features.onnx",
        "decoder_prefill.int4.onnx",
        "decoder_prefill.int4.onnx.data",
        "decoder_audio.int4.onnx",
        "decoder_audio.int4.onnx.data",
        "decoder_step.int4.onnx",
        "decoder_step.int4.onnx.data",
        "embed_tokens.float32.bin",
        "tokenizer.json",
        "tokenizer_config.json",
        "vocab.json",
    ];

    /// <summary>Returns true when <paramref name="modelDir"/> is a complete VibeVoice package.</summary>
    public static bool IsVibeVoiceAsrModel(string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            return false;

        var manifestPath = Path.Combine(modelDir, "vibevoice_onnx_config.json");
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("model_type", out var modelType)
                || !string.Equals(modelType.GetString(), ModelType, StringComparison.OrdinalIgnoreCase))
                return false;

            return RequiredFiles.All(file =>
            {
                var path = Path.Combine(modelDir, file);
                return File.Exists(path) && new FileInfo(path).Length > 0;
            });
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}