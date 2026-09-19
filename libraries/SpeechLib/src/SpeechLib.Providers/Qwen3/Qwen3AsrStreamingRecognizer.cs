using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeechLib.Qwen3;

/// <summary>
/// Qwen3-ASR streaming provider for the separate ONNX streaming model format.
/// Each decode uses a bounded sliding context window. The window advances by
/// chunk_seconds, so older audio is discarded instead of being re-encoded for
/// the entire lifetime of the stream.
/// </summary>
public sealed class Qwen3AsrStreamingRecognizer : IStreamingSpeechRecognizer, ILanguageConfigurable, ITranslationConfigurable
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
    /// Qwen3 ONNX model through <c>base_model_dir</c>, or keep the encoder and
    /// decoder files together in a self-contained package.
    /// </summary>
    public Qwen3AsrStreamingRecognizer(string modelDir, string executionProvider = "cpu", string? language = null)
    {
        var dir = Path.GetFullPath(modelDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Model directory not found: {dir}");

        var config = LoadConfig(dir);
        var baseModelDir = ResolveBaseModelDirectory(dir, config.BaseModelDir);
        if (!Qwen3AsrRecognizer.IsQwen3AsrModel(baseModelDir))
            throw new InvalidDataException($"Streaming model does not reference a Qwen3 ONNX model: {baseModelDir}");

        if (!double.IsFinite(config.ChunkSeconds) || config.ChunkSeconds <= 0)
            throw new InvalidDataException("streaming_config.json chunk_seconds must be greater than zero.");

        if (!double.IsFinite(config.WindowSeconds) || config.WindowSeconds < 0)
            throw new InvalidDataException("streaming_config.json window_seconds must be zero or greater.");

        if (config.WindowSeconds > 0 && config.WindowSeconds < config.ChunkSeconds * 2)
            throw new InvalidDataException("streaming_config.json window_seconds must contain at least two decode chunks.");

        _chunkRecognizer = new Qwen3AsrRecognizer(
            baseModelDir,
            chunkSeconds: config.ChunkSeconds,
            executionProvider: executionProvider,
            windowSeconds: config.WindowSeconds > 0
                ? config.WindowSeconds
                : Math.Max(Qwen3AsrRecognizer.DefaultStreamingWindowSeconds, config.ChunkSeconds * 2),
            language: language,
            streamingEncoderPath: ResolveStreamingEncoderPath(dir, baseModelDir, config.EncoderFile),
            streamingBlockSeconds: config.BlockSeconds,
            decodeEveryBlocks: config.DecodeEveryBlocks,
            skipSilentBlocks: config.SkipSilentBlocks,
            intraOpThreads: config.IntraOpThreads,
            encoderIntraOpThreads: config.EncoderIntraOpThreads,
            emitFirstBlock: config.EmitFirstBlock);
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
                && double.IsFinite(config.ChunkSeconds)
                && config.ChunkSeconds > 0
                && double.IsFinite(config.WindowSeconds)
                && config.WindowSeconds >= 0
                && (config.WindowSeconds == 0 || config.WindowSeconds >= config.ChunkSeconds * 2);
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
    public string? PartialText => _chunkRecognizer.PartialText;

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _chunkRecognizer.ResetStreamingState();
    }

    /// <inheritdoc />
    public bool TrySetLanguage(string language) => _chunkRecognizer.TrySetLanguage(language);

    /// <inheritdoc />
    public bool TrySetTranslation(bool enabled, string targetLanguage) =>
        _chunkRecognizer.TrySetTranslation(enabled, targetLanguage);

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
        if (Qwen3AsrRecognizer.IsQwen3AsrModel(streamingDir))
            return streamingDir;

        if (string.IsNullOrWhiteSpace(baseModelDir))
            return streamingDir;

        return Path.GetFullPath(Path.Combine(streamingDir, baseModelDir));
    }

    private static string? ResolveStreamingEncoderPath(
        string streamingDir,
        string baseModelDir,
        string? encoderFile)
    {
        if (string.IsNullOrWhiteSpace(encoderFile))
            return null;

        var streamingPath = Path.GetFullPath(Path.Combine(streamingDir, encoderFile));
        if (File.Exists(streamingPath))
            return streamingPath;

        return Path.GetFullPath(Path.Combine(baseModelDir, encoderFile));
    }

    private sealed class StreamingConfig
    {
        [JsonPropertyName("model_type")]
        public string? ModelType { get; init; }

        [JsonPropertyName("base_model_dir")]
        public string? BaseModelDir { get; init; }

        [JsonPropertyName("chunk_seconds")]
        public double ChunkSeconds { get; init; } = 2.0;

        [JsonPropertyName("window_seconds")]
        public double WindowSeconds { get; init; }

        [JsonPropertyName("encoder_file")]
        public string? EncoderFile { get; init; }

        [JsonPropertyName("block_seconds")]
        public double BlockSeconds { get; init; } = Qwen3AsrRecognizer.DefaultStreamingChunkSeconds;

        /// <summary>
        /// Encode every block but run the (much more expensive) decoder once per N
        /// blocks. 1 keeps the lowest output latency.
        /// </summary>
        [JsonPropertyName("decode_every_blocks")]
        public int DecodeEveryBlocks { get; init; } = 1;

        /// <summary>
        /// Skip decoding encoder blocks that contain no speech (RMS below ~-46 dBFS).
        /// The next speech block or Flush decodes the full window, so text is deferred
        /// rather than lost.
        /// </summary>
        [JsonPropertyName("skip_silent_blocks")]
        public bool SkipSilentBlocks { get; init; } = true;

        /// <summary>ONNX Runtime intra-op threads. 0 keeps the built-in heuristic.</summary>
        [JsonPropertyName("intra_op_threads")]
        public int IntraOpThreads { get; init; }

        /// <summary>ONNX Runtime intra-op threads for the encoder session. 0 keeps the built-in heuristic.</summary>
        [JsonPropertyName("encoder_intra_op_threads")]
        public int EncoderIntraOpThreads { get; init; }

        /// <summary>
        /// Emit the first decoded block immediately so text appears at the first
        /// block boundary instead of waiting for a second decode to agree. Measured
        /// CV17 cost: English WER 9.74% to 13.23%, Russian 10.53% to 14.83%.
        /// </summary>
        [JsonPropertyName("emit_first_block")]
        public bool EmitFirstBlock { get; init; }
    }
}