using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeechLib.Qwen3;

/// <summary>
/// Qwen3-ASR streaming provider for the separate ONNX streaming model format.
/// It follows the official Qwen streaming contract: each completed chunk is
/// decoded with all audio accumulated so far, and only a stable text delta is
/// returned to the caller.
/// </summary>
public sealed class Qwen3AsrStreamingRecognizer : IStreamingSpeechRecognizer
{
    private const string ConfigFileName = "streaming_config.json";
    private const int InputChunkSamples = 1600;

    private readonly Qwen3AsrRecognizer _chunkRecognizer;
    private bool _disposed;

    /// <inheritdoc />
    public int SampleRate => _chunkRecognizer.SampleRate;

    /// <inheritdoc />
    public int ChunkSamples => InputChunkSamples;

    /// <summary>
    /// Loads a streaming manifest. The manifest can point at a shared regular
    /// Qwen3 ONNX model through <c>base_model_dir</c>, avoiding duplicated
    /// multi-gigabyte graph and embedding files.
    /// </summary>
    public Qwen3AsrStreamingRecognizer(string modelDir, string executionProvider = "cpu")
    {
        var dir = Path.GetFullPath(modelDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Model directory not found: {dir}");

        var config = LoadConfig(dir);
        var baseModelDir = ResolveBaseModelDirectory(dir, config.BaseModelDir);
        if (!Qwen3AsrRecognizer.IsQwen3AsrModel(baseModelDir))
            throw new InvalidDataException($"Streaming model does not reference a Qwen3 ONNX model: {baseModelDir}");

        if (config.ChunkSeconds <= 0)
            throw new InvalidDataException("streaming_config.json chunk_seconds must be greater than zero.");

        _chunkRecognizer = new Qwen3AsrRecognizer(
            baseModelDir,
            chunkSeconds: config.ChunkSeconds,
            executionProvider: executionProvider);
    }

    /// <summary>
    /// True when <paramref name="modelDir"/> contains a valid Qwen3 streaming
    /// manifest. A regular Qwen3 model is intentionally not matched.
    /// </summary>
    public static bool IsQwen3AsrStreamingModel(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir)) return false;
        var path = Path.Combine(modelDir, ConfigFileName);
        if (!File.Exists(path)) return false;

        try
        {
            var config = ReadConfig(path);
            return string.Equals(config.ModelType, "qwen3_asr_onnx_streaming", StringComparison.OrdinalIgnoreCase)
                && config.ChunkSeconds > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string? ProcessAudio(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.Length == 0 ? null : _chunkRecognizer.ProcessAudio(chunk);
    }

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _chunkRecognizer.Flush();
    }

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _chunkRecognizer.ResetStreamingState();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chunkRecognizer.Dispose();
    }

    private static StreamingConfig LoadConfig(string modelDir) =>
        ReadConfig(Path.Combine(modelDir, ConfigFileName));

    private static StreamingConfig ReadConfig(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Streaming model manifest not found: {path}", path);

        var config = JsonSerializer.Deserialize<StreamingConfig>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return config ?? throw new InvalidDataException($"Streaming model manifest is empty: {path}");
    }

    private static string ResolveBaseModelDirectory(string streamingDir, string? baseModelDir)
    {
        if (string.IsNullOrWhiteSpace(baseModelDir))
            return streamingDir;

        return Path.GetFullPath(Path.Combine(streamingDir, baseModelDir));
    }

    private sealed class StreamingConfig
    {
        [JsonPropertyName("model_type")]
        public string? ModelType { get; init; }

        [JsonPropertyName("base_model_dir")]
        public string? BaseModelDir { get; init; }

        [JsonPropertyName("chunk_seconds")]
        public double ChunkSeconds { get; init; } = 2.0;
    }
}