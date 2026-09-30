using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SpeechLib.ModelFormats;
using SpeechLib.Qwen3;

namespace SpeechLib.VibeVoice;

/// <summary>
/// VibeVoice streaming ASR on plain ONNX Runtime.
///
/// The exporter keeps the speech tokenizer in a fixed-window FP32 graph and
/// splits the language model into prompt-prefill, audio-insertion, and token
/// step graphs. The decoder graphs use explicit causal masks and 56 flattened
/// key/value cache tensors.
/// </summary>
public sealed class VibeVoiceAsrRecognizer : IStreamingSpeechRecognizer, IRuntimeConfigurable
{
    private const int SampleRateValue = 24_000;
    private const int FrameSamples = 3_200;
    private const int ChunkFrames = 22;
    private const int LookaheadFrames = 4;
    private const int ChunkSamplesValue = FrameSamples * ChunkFrames;
    private const int WindowSamples = (ChunkFrames + LookaheadFrames) * FrameSamples;
    private const int FeatureFrames = ChunkFrames + LookaheadFrames;
    private const int HiddenSize = 1_536;
    private const int VocabularySize = 151_936;
    private const int LayerCount = 28;
    private const int KeyValueHeads = 2;
    private const int HeadDimension = 128;
    private const int MaxTokensPerChunk = 128;

    private const string Prompt =
        "You are a helpful assistant that transcribes audio input into text output. "
        + "Please transcribe the following audios streamingly with these keys: speaker, content\n";

    private readonly string _executionProvider;
    private readonly string _modelDirectory;
    private readonly InferenceSession _speechFeatures;
    private readonly Qwen3BpeTokenizer _tokenizer;
    private readonly float[] _embedTokens;

    // The three decoder graphs each carry their own 840 MB int4 external-data file, and
    // opening one costs ~2 s (measured on this machine). Creating them per streaming
    // chunk made session setup dominate the run: 9.1 s of work for 2.18 s of audio.
    // They are created once per recognizer instead.
    private InferenceSession? _decoderPrefillSession;
    private InferenceSession? _decoderAudioSession;
    private InferenceSession? _decoderStepSession;
    private readonly int _speechStartId;
    private readonly int _speechEndId;
    private readonly int _textChunkEndId;
    private readonly int _eosId;
    private readonly List<float> _audio = new();

    private DecoderCache? _cache;
    private long _bufferStartSample;
    private long _nextChunkStart;
    private int _lastTokenCount;
    private bool _disposed;

    /// <inheritdoc />
    public int SampleRate => SampleRateValue;

    /// <inheritdoc />
    public int ChunkSamples => FrameSamples;

    /// <inheritdoc />
    public int LastTokenCount => _lastTokenCount;

    /// <summary>Loads a complete VibeVoice INT4 ONNX package.</summary>
    public VibeVoiceAsrRecognizer(string modelDir, string executionProvider = "cpu")
    {
        _modelDirectory = Path.GetFullPath(modelDir);
        if (!VibeVoiceModelDetector.IsVibeVoiceAsrModel(_modelDirectory))
            throw new InvalidDataException($"Incomplete VibeVoice ONNX model: {_modelDirectory}");

        _executionProvider = executionProvider;
        _speechFeatures = OpenSession(Path.Combine(_modelDirectory, "speech_features.onnx"));
        _tokenizer = Qwen3BpeTokenizer.Load(_modelDirectory);
        _embedTokens = LoadEmbeddingTable(Path.Combine(_modelDirectory, "embed_tokens.float32.bin"));

        var tokenIds = LoadTokenIds(Path.Combine(_modelDirectory, "tokenizer.json"));
        _speechStartId = GetRequiredTokenId(tokenIds, "<|object_ref_start|>");
        _speechEndId = GetRequiredTokenId(tokenIds, "<|object_ref_end|>");
        _textChunkEndId = GetRequiredTokenId(tokenIds, "<|text_chunk_end|>");
        _eosId = GetRequiredTokenId(tokenIds, "<|endoftext|>");
    }

    /// <inheritdoc />
    public string? ProcessAudio(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0)
            return null;

        _audio.AddRange(chunk);
        return DecodeAvailable(flush: false);
    }

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return DecodeAvailable(flush: true);
    }

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.Clear();
        _cache = null;
        _bufferStartSample = 0;
        _nextChunkStart = 0;
        _lastTokenCount = 0;
    }

    /// <summary>
    /// VibeVoice consumes 24 kHz audio and the shared Silero VAD is fixed at
    /// 16 kHz, so the generic VAD decorator must not be applied to this provider.
    /// </summary>
    public bool TrySetVad(bool enabled) => false;

    /// <inheritdoc />
    public bool TrySetSearchOptions(int numBeams, double repetitionPenalty) => false;

    private string? DecodeAvailable(bool flush)
    {
        long availableEnd = _bufferStartSample + _audio.Count;
        var text = new StringBuilder();

        while (_nextChunkStart < availableEnd
            && (flush || _nextChunkStart + WindowSamples <= availableEnd))
        {
            text.Append(DecodeChunk(CopyWindow(_nextChunkStart)));
            _nextChunkStart += ChunkSamplesValue;
            TrimConsumedAudio();
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private string DecodeChunk(float[] audioWindow)
    {
        EnsurePromptCache();
        var features = RunSpeechFeatures(audioWindow);
        var audioEmbeddings = new float[(FeatureFrames + 2) * HiddenSize];
        CopyEmbedding(_speechStartId, audioEmbeddings, 0);
        Array.Copy(features, 0, audioEmbeddings, HiddenSize, features.Length);
        CopyEmbedding(_speechEndId, audioEmbeddings, (FeatureFrames + 1) * HiddenSize);

        var audioResult = RunCachedGraph(
            AudioSession,
            audioEmbeddings,
            FeatureFrames + 2,
            _cache!);
        _cache = audioResult.Cache;
        var nextLogits = audioResult.Logits;
        var tokenIds = new List<int>();

        var stepSession = StepSession;
        for (int index = 0; index < MaxTokensPerChunk; index++)
        {
            int nextToken = ArgMax(nextLogits);
            if (nextToken == _textChunkEndId || nextToken == _eosId)
                break;

            tokenIds.Add(nextToken);
            var stepResult = RunCachedGraph(
                stepSession,
                EmbedToken(nextToken),
                1,
                _cache);
            _cache = stepResult.Cache;
            nextLogits = stepResult.Logits;
        }

        var endResult = RunCachedGraph(
            stepSession,
            EmbedToken(_textChunkEndId),
            1,
            _cache);
        _cache = endResult.Cache;

        _lastTokenCount = tokenIds.Count;
        return _tokenizer.Decode(tokenIds.Select(static id => (long)id));
    }

    private void EnsurePromptCache()
    {
        if (_cache is not null)
            return;

        var promptIds = _tokenizer.Encode(Prompt);
        var result = RunPrefill(EmbeddingLookup(promptIds), promptIds.Count);
        _cache = result.Cache;
    }

    private DecoderResult RunPrefill(float[] embeddings, int sequenceLength)
    {
        var session = PrefillSession;
        var embedTensor = new DenseTensor<float>(embeddings, new[] { 1, sequenceLength, HiddenSize });
        var positionTensor = new DenseTensor<long>(CreatePositions(sequenceLength, 0), new[] { 1, sequenceLength });
        var maskTensor = CreateAttentionMask(0, sequenceLength);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("inputs_embeds", embedTensor),
            NamedOnnxValue.CreateFromTensor("position_ids", positionTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
        };

        using var results = session.Run(inputs);
        return ReadDecoderResult(results);
    }

    private static DecoderResult RunCachedGraph(
        InferenceSession session,
        float[] embeddings,
        int queryLength,
        DecoderCache cache)
    {
        int pastLength = cache.SequenceLength;
        var embedTensor = new DenseTensor<float>(embeddings, new[] { 1, queryLength, HiddenSize });
        var positionTensor = new DenseTensor<long>(
            CreatePositions(queryLength, pastLength),
            new[] { 1, queryLength });
        var maskTensor = CreateAttentionMask(pastLength, queryLength);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("inputs_embeds", embedTensor),
            NamedOnnxValue.CreateFromTensor("position_ids", positionTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
        };

        for (int layer = 0; layer < LayerCount; layer++)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                $"past_key_{layer}",
                new DenseTensor<float>(cache.Keys[layer], new[] { 1, KeyValueHeads, pastLength, HeadDimension })));
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                $"past_value_{layer}",
                new DenseTensor<float>(cache.Values[layer], new[] { 1, KeyValueHeads, pastLength, HeadDimension })));
        }

        using var results = session.Run(inputs);
        return ReadDecoderResult(results);
    }

    private float[] RunSpeechFeatures(float[] audioWindow)
    {
        var audioTensor = new DenseTensor<float>(audioWindow, new[] { 1, 1, WindowSamples });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("audio", audioTensor) };
        using var results = _speechFeatures.Run(inputs);
        var features = results[0].AsTensor<float>().ToArray();
        if (features.Length != FeatureFrames * HiddenSize)
        {
            throw new InvalidDataException(
                $"Unexpected VibeVoice feature shape: {features.Length} values; "
                + $"expected {FeatureFrames * HiddenSize}.");
        }

        return features;
    }

    private static DecoderResult ReadDecoderResult(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var keys = new float[LayerCount][];
        var values = new float[LayerCount][];
        for (int layer = 0; layer < LayerCount; layer++)
        {
            keys[layer] = results[1 + layer * 2].AsTensor<float>().ToArray();
            values[layer] = results[2 + layer * 2].AsTensor<float>().ToArray();
        }

        return new DecoderResult(
            results[0].AsTensor<float>().ToArray(),
            new DecoderCache(keys, values));
    }

    private float[] CopyWindow(long startSample)
    {
        int offset = checked((int)(startSample - _bufferStartSample));
        if (offset < 0 || offset > _audio.Count)
            throw new InvalidOperationException("VibeVoice audio buffer window is out of range.");

        var window = new float[WindowSamples];
        int available = Math.Min(WindowSamples, _audio.Count - offset);
        _audio.CopyTo(offset, window, 0, available);
        return window;
    }

    private void TrimConsumedAudio()
    {
        int removeCount = checked((int)(_nextChunkStart - _bufferStartSample));
        if (removeCount <= 0)
            return;

        removeCount = Math.Min(removeCount, _audio.Count);
        _audio.RemoveRange(0, removeCount);
        _bufferStartSample += removeCount;
    }

    private float[] EmbeddingLookup(IReadOnlyList<long> tokenIds)
    {
        var embeddings = new float[tokenIds.Count * HiddenSize];
        for (int index = 0; index < tokenIds.Count; index++)
            CopyEmbedding(checked((int)tokenIds[index]), embeddings, index * HiddenSize);
        return embeddings;
    }

    private float[] EmbedToken(int tokenId)
    {
        var embedding = new float[HiddenSize];
        CopyEmbedding(tokenId, embedding, 0);
        return embedding;
    }

    private void CopyEmbedding(int tokenId, float[] destination, int destinationOffset)
    {
        if ((uint)tokenId >= VocabularySize)
            throw new InvalidDataException($"VibeVoice token id is outside the vocabulary: {tokenId}");

        Array.Copy(_embedTokens, tokenId * HiddenSize, destination, destinationOffset, HiddenSize);
    }

    private static long[] CreatePositions(int count, int start)
    {
        var positions = new long[count];
        for (int index = 0; index < count; index++)
            positions[index] = start + index;
        return positions;
    }

    private static DenseTensor<float> CreateAttentionMask(int pastLength, int queryLength)
    {
        int keyLength = pastLength + queryLength;
        var values = new float[queryLength * keyLength];
        Array.Fill(values, float.NegativeInfinity);
        for (int query = 0; query < queryLength; query++)
        {
            int allowed = pastLength + query + 1;
            Array.Fill(values, 0f, query * keyLength, allowed);
        }

        return new DenseTensor<float>(values, new[] { 1, 1, queryLength, keyLength });
    }

    private static int ArgMax(float[] logits)
    {
        int offset = logits.Length - VocabularySize;
        int best = 0;
        float bestValue = float.NegativeInfinity;
        for (int index = 0; index < VocabularySize; index++)
        {
            float value = logits[offset + index];
            if (value > bestValue)
            {
                bestValue = value;
                best = index;
            }
        }

        return best;
    }

    private InferenceSession OpenSession(string path)
    {
        using var options = CreateSessionOptions(_executionProvider);
        return new InferenceSession(path, options);
    }

    private InferenceSession PrefillSession =>
        _decoderPrefillSession ??= OpenSession(Path.Combine(_modelDirectory, "decoder_prefill.int4.onnx"));

    private InferenceSession AudioSession =>
        _decoderAudioSession ??= OpenSession(Path.Combine(_modelDirectory, "decoder_audio.int4.onnx"));

    private InferenceSession StepSession =>
        _decoderStepSession ??= OpenSession(Path.Combine(_modelDirectory, "decoder_step.int4.onnx"));

    private static SessionOptions CreateSessionOptions(string executionProvider)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        OrtCpuTuning.DisableThreadSpinning(options);

        // A persisted provider setting may name a provider that is no longer
        // shipped (for example "cuda" after a GPU build); degrade to CPU
        // instead of failing session creation.
        ExecutionProviderSelector.Apply(options, executionProvider);

        return options;
    }

    private static float[] LoadEmbeddingTable(string path)
    {
        const int expectedValues = VocabularySize * HiddenSize;
        long expectedBytes = (long)expectedValues * sizeof(float);
        if (new FileInfo(path).Length != expectedBytes)
            throw new InvalidDataException($"Unexpected VibeVoice embedding table size: {path}");

        var values = new float[expectedValues];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static Dictionary<string, int> LoadTokenIds(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in document.RootElement.GetProperty("added_tokens").EnumerateArray())
        {
            var content = token.GetProperty("content").GetString();
            if (!string.IsNullOrEmpty(content))
                ids[content] = token.GetProperty("id").GetInt32();
        }

        return ids;
    }

    private static int GetRequiredTokenId(IReadOnlyDictionary<string, int> ids, string token)
        => ids.TryGetValue(token, out var id)
            ? id
            : throw new InvalidDataException($"VibeVoice tokenizer token is missing: {token}");

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _speechFeatures.Dispose();
        _decoderPrefillSession?.Dispose();
        _decoderAudioSession?.Dispose();
        _decoderStepSession?.Dispose();
    }

    private sealed record DecoderResult(float[] Logits, DecoderCache Cache);

    private sealed class DecoderCache(float[][] keys, float[][] values)
    {
        public float[][] Keys { get; } = keys;
        public float[][] Values { get; } = values;

        public int SequenceLength => Keys[0].Length / (KeyValueHeads * HeadDimension);
    }
}