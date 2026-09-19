using System.Text.Json;

namespace SpeechLib.ModelDownload;

/// <summary>Detects Qwen3-ASR ONNX packages and streaming manifests.</summary>
public static class Qwen3ModelDetector
{
    public const string StreamingModelType = "qwen3_asr_onnx_streaming";

    private static readonly string[] RequiredModelFiles =
    [
        "encoder.int4.onnx",
        "decoder_init.int4.onnx",
        "decoder_step.int4.onnx",
        "decoder_weights.int4.data",
        "embed_tokens.bin",
        "vocab.json",
    ];

    /// <summary>True when <paramref name="modelDir"/> contains a regular Qwen3 package.</summary>
    public static bool IsQwen3AsrModel(string? modelDir) =>
        HasRequiredFiles(modelDir);

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
            if (root.TryGetProperty("model_type", out var modelType)
                && string.Equals(modelType.GetString(), StreamingModelType, StringComparison.OrdinalIgnoreCase)
                && IsValidStreamingTiming(root)
                && IsValidStreamingEncoder(root))
                return true;

            return false;
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
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(modelDir!, "streaming_config.json")));
            var baseModelDir = ResolveBaseModelDirectory(modelDir!);
            if (!HasRequiredFiles(baseModelDir) && !HasRequiredFiles(modelDir))
                return false;

            if (!rootHasEncoderFile(document.RootElement, modelDir!, baseModelDir))
                return false;

            return true;
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

    private static bool IsValidStreamingTiming(JsonElement root)
    {
        const double defaultChunkSeconds = 2.0;
        double chunkSeconds = defaultChunkSeconds;
        if (root.TryGetProperty("chunk_seconds", out var chunkValue))
        {
            if (chunkValue.ValueKind != JsonValueKind.Number)
                return false;

            chunkSeconds = chunkValue.GetDouble();
            if (!double.IsFinite(chunkSeconds) || chunkSeconds <= 0)
                return false;
        }

        if (!root.TryGetProperty("window_seconds", out var windowValue))
            return true;

        if (windowValue.ValueKind != JsonValueKind.Number)
            return false;

        var windowSeconds = windowValue.GetDouble();
        return double.IsFinite(windowSeconds)
            && windowSeconds >= 0
            && (windowSeconds == 0 || windowSeconds >= chunkSeconds * 2);
    }

    private static bool IsValidStreamingEncoder(JsonElement root)
    {
        if (!root.TryGetProperty("encoder_file", out var encoderValue))
            return true;

        if (encoderValue.ValueKind != JsonValueKind.String)
            return false;

        return !string.IsNullOrWhiteSpace(encoderValue.GetString());
    }

    private static bool rootHasEncoderFile(JsonElement root, string streamingDir, string baseModelDir)
    {
        if (!root.TryGetProperty("encoder_file", out var encoderValue))
            return true;

        if (encoderValue.ValueKind != JsonValueKind.String)
            return false;

        var encoderFile = encoderValue.GetString();
        if (string.IsNullOrWhiteSpace(encoderFile))
            return false;

        return File.Exists(Path.Combine(streamingDir, encoderFile))
            || File.Exists(Path.Combine(baseModelDir, encoderFile));
    }

    private static bool HasRequiredFiles(string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            return false;

        return RequiredModelFiles.All(file =>
        {
            var path = Path.Combine(modelDir, file);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });
    }
}