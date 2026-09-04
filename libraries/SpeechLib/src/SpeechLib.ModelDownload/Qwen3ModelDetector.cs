using System.Text.Json;

namespace SpeechLib.ModelDownload;

/// <summary>Detects the manifest-based Qwen3-ASR ONNX streaming profile.</summary>
public static class Qwen3ModelDetector
{
    public const string StreamingModelType = "qwen3_asr_onnx_streaming";

    public static bool IsQwen3AsrStreamingModel(string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir))
            return false;

        var configPath = Path.Combine(modelDir, "streaming_config.json");
        if (!File.Exists(configPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            var hasValidChunkSeconds = !root.TryGetProperty("chunk_seconds", out var chunkSeconds)
                || chunkSeconds.ValueKind == JsonValueKind.Number
                    && chunkSeconds.GetDouble() > 0;
            return root.TryGetProperty("model_type", out var modelType)
                && string.Equals(modelType.GetString(), StreamingModelType, StringComparison.OrdinalIgnoreCase)
                && hasValidChunkSeconds;
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

    /// <summary>
    /// Checks the files required by the current C# Qwen3 INT4 provider,
    /// including the base model referenced by the streaming manifest.
    /// </summary>
    public static bool IsCompleteQwen3AsrStreamingModel(string? modelDir)
    {
        if (!IsQwen3AsrStreamingModel(modelDir))
            return false;

        try
        {
            var baseModelDir = ResolveBaseModelDirectory(modelDir!);
            var requiredFiles = new[]
            {
                "encoder.int4.onnx",
                "decoder_init.int4.onnx",
                "decoder_step.int4.onnx",
                "decoder_weights.int4.data",
                "embed_tokens.bin",
                "vocab.json",
            };

            return requiredFiles.All(file =>
            {
                var path = Path.Combine(baseModelDir, file);
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

    private static string ResolveBaseModelDirectory(string streamingDir)
    {
        var configPath = Path.Combine(streamingDir, "streaming_config.json");
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var baseModelDir = root.TryGetProperty("base_model_dir", out var value)
            ? value.GetString()
            : null;

        return string.IsNullOrWhiteSpace(baseModelDir)
            ? Path.GetFullPath(streamingDir)
            : Path.GetFullPath(Path.Combine(streamingDir, baseModelDir));
    }
}