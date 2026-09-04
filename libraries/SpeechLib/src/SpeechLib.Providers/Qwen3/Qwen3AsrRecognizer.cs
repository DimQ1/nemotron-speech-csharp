using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpeechLib.Qwen3;

/// <summary>
/// Qwen3-ASR (1.7B) recognizer on plain ONNX Runtime.
///
/// Executes the andrewleech/qwen3-asr-1.7b-onnx export (int4 variant):
///   - encoder.int4.onnx          (mel -> audio_features [1, N, 2048])
///   - decoder_init.int4.onnx     (prefill: input_ids + audio_features -> logits + KV cache)
///   - decoder_step.int4.onnx     (autoregressive step: input_embeds + KV cache)
///   - embed_tokens.bin           (token embedding matrix [151936, 2048], float16)
///   - vocab.json + merges.txt    (byte-level BPE tokenizer)
///
/// Pipeline (ported from qwen3-asr-onnx/src/inference.py):
///   1. log-mel spectrogram (Whisper-compatible, host-side)
///   2. encoder: mel -> audio_features
///   3. build prompt ids with N x <|audio_pad|> placeholders
///   4. decoder_init prefill -> first token + KV cache
///   5. greedy decoder_step until EOS
///
/// Streaming: this export is an encoder-decoder model, not a transducer, so it
/// has no per-chunk incremental decode. Streaming is implemented as buffered
/// windowed re-transcription with a stable-prefix emission policy (only the
/// confirmed prefix that stops changing between windows is emitted).
/// </summary>
public sealed class Qwen3AsrRecognizer : IStreamingSpeechRecognizer
{
    private const int AudioHidden = 2048;
    private const int VocabSize = 151936;
    private const int MaxTokens = 256;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderInit;
    private readonly InferenceSession _decoderStep;
    private readonly Qwen3BpeTokenizer _tokenizer;

    // Token embedding matrix kept in managed memory for per-token lookup.
    private readonly float[] _embedTokens; // [VocabSize, AudioHidden]

    private readonly List<float> _audio = new();
    private readonly int _chunkSamples;
    private bool _disposed;

    // Streaming state.
    private string _emittedStable = "";
    private string _lastFullText = "";
    private int _decodedSamples;

    /// <inheritdoc />
    public int SampleRate => WhisperMel.SampleRate;

    /// <inheritdoc />
    public int ChunkSamples => 1600; // 100 ms at 16 kHz

    /// <summary>
    /// Loads the int4 ONNX export. The folder must contain encoder.int4.onnx,
    /// decoder_init.int4.onnx, decoder_step.int4.onnx, decoder_weights.int4.data,
    /// embed_tokens.bin, vocab.json, merges.txt and tokenizer_config.json.
    /// </summary>
    public Qwen3AsrRecognizer(string modelDir, double chunkSeconds = 4.0, string executionProvider = "cpu")
    {
        var dir = Path.GetFullPath(modelDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Model directory not found: {dir}");

        var options = CreateSessionOptions(executionProvider);
        _encoder = new InferenceSession(Path.Combine(dir, "encoder.int4.onnx"), options);
        _decoderInit = new InferenceSession(Path.Combine(dir, "decoder_init.int4.onnx"), options);
        _decoderStep = new InferenceSession(Path.Combine(dir, "decoder_step.int4.onnx"), options);

        _tokenizer = Qwen3BpeTokenizer.Load(dir);
        _embedTokens = LoadEmbedTokens(Path.Combine(dir, "embed_tokens.bin"));
        _chunkSamples = (int)(WhisperMel.SampleRate * chunkSeconds);
    }

    /// <summary>Transcribe a complete 16 kHz mono float waveform.</summary>
    public string Transcribe(float[] waveform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TranscribeCore(waveform);
    }

    /// <inheritdoc />
    public string? ProcessAudio(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.AddRange(chunk);

        int available = _audio.Count - _decodedSamples;
        if (available < _chunkSamples)
            return null;

        _decodedSamples = _audio.Count;
        return EmitStableDelta(TranscribeCore(_audio.ToArray()));
    }

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decodedSamples >= _audio.Count && _lastFullText.Length > 0)
            return _lastFullText.Length > _emittedStable.Length
                ? _lastFullText[_emittedStable.Length..]
                : "";

        _decodedSamples = _audio.Count;
        var text = TranscribeCore(_audio.ToArray());
        var delta = text.Length > _emittedStable.Length ? text[_emittedStable.Length..] : "";
        _emittedStable = text;
        _lastFullText = text;
        return delta;
    }

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.Clear();
        _decodedSamples = 0;
        _emittedStable = "";
        _lastFullText = "";
    }

    /// <summary>
    /// True when modelDir contains a Qwen3-ASR ONNX export (encoder.int4.onnx
    /// plus the decoder pair and tokenizer files).
    /// </summary>
    public static bool IsQwen3AsrModel(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir)) return false;
        return File.Exists(Path.Combine(modelDir, "encoder.int4.onnx"))
            && File.Exists(Path.Combine(modelDir, "decoder_init.int4.onnx"))
            && File.Exists(Path.Combine(modelDir, "decoder_step.int4.onnx"))
            && File.Exists(Path.Combine(modelDir, "vocab.json"));
    }

    // ------------------------------------------------------------------ //
    // Inference pipeline                                                  //
    // ------------------------------------------------------------------ //

    private string TranscribeCore(float[] waveform)
    {
        var mel = WhisperMel.ComputeLogMel(waveform, out int frames);
        int audioTokens = Qwen3Prompt.GetEncoderOutputLength(frames);

        var audioFeatures = RunEncoder(mel, frames); // [1, audioTokens, 2048]
        var promptIds = Qwen3Prompt.BuildPromptIds(audioTokens, out int audioOffset);

        var (logits, keys, values) = RunDecoderInit(promptIds, audioFeatures, audioTokens, audioOffset);
        int next = ArgMaxLastLogit(logits, promptIds.Length);
        if (Qwen3Prompt.IsEos(next))
            return "";

        var output = new List<long> { next };
        long pos = promptIds.Length;

        for (int i = 0; i < MaxTokens - 1; i++)
        {
            var embed = EmbedToken(next);
            (logits, keys, values) = RunDecoderStep(embed, pos, keys, values);
            next = ArgMaxLastLogit(logits, 1);
            output.Add(next);
            pos++;
            if (Qwen3Prompt.IsEos(next)) break;
        }

        // Drop trailing EOS before detokenizing.
        while (output.Count > 0 && Qwen3Prompt.IsEos((int)output[^1]))
            output.RemoveAt(output.Count - 1);

        return _tokenizer.Decode(output).Trim();
    }

    private float[] RunEncoder(float[] mel, int frames)
    {
        var melTensor = new DenseTensor<float>(mel, new[] { 1, WhisperMel.NMels, frames });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("mel", melTensor) };
        using var results = _encoder.Run(inputs);
        return results[0].AsTensor<float>().ToArray(); // [1, audioTokens, 2048]
    }

    private (float[] logits, float[] keys, float[] values) RunDecoderInit(
        long[] promptIds, float[] audioFeatures, int audioTokens, int audioOffset)
    {
        int seq = promptIds.Length;
        var idTensor = new DenseTensor<long>(promptIds, new[] { 1, seq });
        var posTensor = new DenseTensor<long>(new[] { 1, seq });
        for (int i = 0; i < seq; i++) posTensor[0, i] = i;

        var audioTensor = new DenseTensor<float>(audioFeatures, new[] { 1, audioTokens, AudioHidden });
        var offsetTensor = new DenseTensor<long>(new[] { (long)audioOffset }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", idTensor),
            NamedOnnxValue.CreateFromTensor("position_ids", posTensor),
            NamedOnnxValue.CreateFromTensor("audio_features", audioTensor),
            NamedOnnxValue.CreateFromTensor("audio_offset", offsetTensor),
        };

        using var results = _decoderInit.Run(inputs);
        return ExtractOutputs(results);
    }

    private (float[] logits, float[] keys, float[] values) RunDecoderStep(
        float[] embed, long pos, float[] keys, float[] values)
    {
        var embedTensor = new DenseTensor<float>(embed, new[] { 1, 1, AudioHidden });
        var posTensor = new DenseTensor<long>(new[] { pos }, new[] { 1, 1 });

        int pastLen = keys.Length / (28 * 8 * 128);
        var keyTensor = new DenseTensor<float>(keys, new[] { 28, 1, 8, pastLen, 128 });
        var valTensor = new DenseTensor<float>(values, new[] { 28, 1, 8, pastLen, 128 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_embeds", embedTensor),
            NamedOnnxValue.CreateFromTensor("position_ids", posTensor),
            NamedOnnxValue.CreateFromTensor("past_keys", keyTensor),
            NamedOnnxValue.CreateFromTensor("past_values", valTensor),
        };

        using var results = _decoderStep.Run(inputs);
        return ExtractOutputs(results);
    }

    private static (float[] logits, float[] keys, float[] values) ExtractOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        float[] logits = results[0].AsTensor<float>().ToArray();
        float[] keys = results[1].AsTensor<float>().ToArray();
        float[] values = results[2].AsTensor<float>().ToArray();
        return (logits, keys, values);
    }

    /// <summary>ArgMax over the last position's vocab logits.</summary>
    private static int ArgMaxLastLogit(float[] logits, int seqLen)
    {
        int offset = (seqLen - 1) * VocabSize;
        int best = 0;
        float bestVal = float.NegativeInfinity;
        for (int i = 0; i < VocabSize; i++)
        {
            float v = logits[offset + i];
            if (v > bestVal) { bestVal = v; best = i; }
        }
        return best;
    }

    /// <summary>Looks up one token embedding row from the fp16 matrix.</summary>
    private float[] EmbedToken(int token)
    {
        var row = new float[AudioHidden];
        Array.Copy(_embedTokens, token * AudioHidden, row, 0, AudioHidden);
        return row;
    }

    /// <summary>
    /// Emits only the stable prefix that stopped changing since the previous
    /// window, so partial results never regress.
    /// </summary>
    private string? EmitStableDelta(string fullText)
    {
        // Longest common prefix between previous and current full transcription.
        int common = 0;
        int limit = Math.Min(_lastFullText.Length, fullText.Length);
        while (common < limit && _lastFullText[common] == fullText[common]) common++;

        _lastFullText = fullText;

        // Never emit a partial word: cut the stable prefix at the last space.
        int stableEnd = fullText.LastIndexOf(' ', Math.Max(0, common - 1)) + 1;
        if (stableEnd <= _emittedStable.Length)
            return null;

        var delta = fullText[_emittedStable.Length..stableEnd];
        _emittedStable = fullText[..stableEnd];
        return delta.Length == 0 ? null : delta;
    }

    // ------------------------------------------------------------------ //
    // Setup                                                               //
    // ------------------------------------------------------------------ //

    private static float[] LoadEmbedTokens(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var half = new Half[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, half, 0, bytes.Length);
        var floats = new float[half.Length];
        for (int i = 0; i < half.Length; i++)
            floats[i] = (float)half[i];
        return floats;
    }

    private static SessionOptions CreateSessionOptions(string executionProvider)
    {
        int threads = Math.Max(2, Environment.ProcessorCount / 2);
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        switch (executionProvider?.Trim().ToLowerInvariant())
        {
            case "cuda": options.AppendExecutionProvider_CUDA(0); break;
            case "dml": options.AppendExecutionProvider_DML(0); break;
        }
        return options;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
        _decoderInit.Dispose();
        _decoderStep.Dispose();
    }
}
