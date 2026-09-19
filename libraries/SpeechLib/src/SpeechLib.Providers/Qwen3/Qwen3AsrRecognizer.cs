using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Runtime.InteropServices;

namespace SpeechLib.Qwen3;

/// <summary>
/// Qwen3-ASR (1.7B) recognizer on plain ONNX Runtime.
///
/// Executes the andrewleech/qwen3-asr-1.7b-onnx export (int4 variant):
///   - encoder.int4.onnx          (mel -> audio_features [1, N, 2048])
///   - decoder_init.int4.onnx     (prefill: input_ids + audio_features -> logits + KV cache)
///   - decoder_step.int4.onnx     (autoregressive step: input_embeds + KV cache)
///   - embed_tokens.bin           (token embedding matrix [151936, 2048], float16)
///   - vocab.json + tokenizer.json (byte-level BPE tokenizer)
///
/// Pipeline (ported from qwen3-asr-onnx/src/inference.py):
///   1. log-mel spectrogram (Whisper-compatible, host-side)
///   2. encoder: mel -> audio_features
///   3. build prompt ids with N x <|audio_pad|> placeholders
///   4. decoder_init prefill -> first token + KV cache
///   5. greedy decoder_step until EOS
///
/// Streaming: this export is an encoder-decoder model, not a transducer, so it
/// has no per-chunk incremental encoder state. Streaming therefore uses a
/// bounded sliding context window: only the latest window is re-transcribed,
/// old audio is discarded, and the overlapping text prefix is committed once
/// the next window confirms it. Work and memory stay bounded by the window.
/// </summary>
public sealed class Qwen3AsrRecognizer : IStreamingSpeechRecognizer, ILanguageConfigurable, ITranslationConfigurable
{
    public const double DefaultStreamingChunkSeconds = 2.0;
    public const double DefaultStreamingWindowSeconds = 16.0;

    private const int AudioHidden = 2048;
    private const int VocabSize = 151936;
    private const int MaxTokens = 256;

    /// <summary>Blocks quieter than this RMS (~-46 dBFS) contain no speech and are not decoded.</summary>
    private const float SilenceRmsThreshold = 0.005f;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderInit;
    private readonly InferenceSession _decoderStep;
    private readonly Qwen3BpeTokenizer _tokenizer;

    // Token embedding matrix kept in managed memory for per-token lookup.
    private readonly Half[] _embedTokens; // [VocabSize, AudioHidden] float16, as stored on disk

    private readonly List<float> _audio = new();
    private readonly List<float> _blockAudio = new();
    private readonly List<float> _streamFeatures = new();
    private readonly int _chunkSamples;
    private readonly int _initialWindowSamples;
    private readonly int _windowSamples;
    private readonly int _blockSamples;
    private readonly int _streamWindowTokens;
    private readonly bool _blockStreaming;
    private readonly int _decodeEveryBlocks;
    private readonly bool _skipSilentBlocks;
    private int _blocksSinceDecode;
    private bool _disposed;

    // Streaming state.
    private readonly Qwen3StreamingTextAligner _textAligner;
    private long[]? _systemPromptIds;
    private long[]? _languagePromptIds;
    private string? _recognitionLanguage;
    private string? _translationLanguage;
    private bool _translationEnabled;
    private bool _autoLanguageDetection = true;
    private readonly Qwen3AutoLanguageTracker _autoLanguageTracker = new();
    private long _totalSamples;
    private long _lastDecodedTotalSamples;
    private long _encodedAudioSamples;
    private int _streamFeatureTokens;

    /// <inheritdoc />
    public int SampleRate => WhisperMel.SampleRate;

    /// <inheritdoc />
    public int ChunkSamples => 1600; // 100 ms at 16 kHz

    /// <summary>
    /// Loads the int4 ONNX export. The folder must contain encoder.int4.onnx,
    /// decoder_init.int4.onnx, decoder_step.int4.onnx, decoder_weights.int4.data,
    /// embed_tokens.bin, vocab.json and tokenizer_config.json.
    /// </summary>
    public Qwen3AsrRecognizer(
        string modelDir,
        double chunkSeconds = DefaultStreamingChunkSeconds,
        string executionProvider = "cpu",
        double windowSeconds = 0,
        string? language = null,
        string? streamingEncoderPath = null,
        double streamingBlockSeconds = 0,
        int decodeEveryBlocks = 1,
        bool skipSilentBlocks = true,
        int intraOpThreads = 0,
        int encoderIntraOpThreads = 0,
        int decoderInitIntraOpThreads = 0,
        bool emitFirstBlock = false)
    {
        var dir = Path.GetFullPath(modelDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Model directory not found: {dir}");

        // The encoder and decoder are separate sessions with different scaling:
        // the encoder is compute-bound (wants cores), the decoder is
        // memory-bandwidth bound (measured: forcing 19 threads on both sessions
        // made total RTF twice as bad). They are configured independently.
        //
        // decoder_init is the *prefill* half of the decoder and behaves like the
        // encoder: it is compute-bound, so it gets the full core count by default
        // (measured: it dominates the streaming budget). Sharing the step session's
        // half-core setting left about a third of the prefill cost on the table.
        var encoderOptions = CreateSessionOptions(executionProvider, encoderIntraOpThreads);
        var decoderOptions = CreateSessionOptions(executionProvider, intraOpThreads);
        var decoderInitOptions = CreateSessionOptions(
            executionProvider,
            decoderInitIntraOpThreads > 0 ? decoderInitIntraOpThreads : Environment.ProcessorCount);
        _blockStreaming = !string.IsNullOrWhiteSpace(streamingEncoderPath);
        _blockSamples = _blockStreaming
            ? SecondsToSamples(streamingBlockSeconds, nameof(streamingBlockSeconds))
            : 0;
        var encoderPath = _blockStreaming
            ? Path.GetFullPath(streamingEncoderPath!)
            : Path.Combine(dir, "encoder.int4.onnx");
        if (_blockStreaming && !File.Exists(encoderPath))
            throw new FileNotFoundException("Streaming encoder graph not found.", encoderPath);

        _encoder = new InferenceSession(encoderPath, encoderOptions);
        _decoderInit = new InferenceSession(Path.Combine(dir, "decoder_init.int4.onnx"), decoderInitOptions);
        _decoderStep = new InferenceSession(Path.Combine(dir, "decoder_step.int4.onnx"), decoderOptions);

        _tokenizer = Qwen3BpeTokenizer.Load(dir);
        _embedTokens = LoadEmbedTokens(Path.Combine(dir, "embed_tokens.bin"));
        _chunkSamples = SecondsToSamples(chunkSeconds, nameof(chunkSeconds));
        _initialWindowSamples = _chunkSamples * 2;
        var effectiveWindowSeconds = windowSeconds > 0
            ? windowSeconds
            : Math.Max(DefaultStreamingWindowSeconds, chunkSeconds * 2);
        _windowSamples = SecondsToSamples(effectiveWindowSeconds, nameof(windowSeconds));
        if (_windowSamples < _chunkSamples * 2)
            throw new ArgumentOutOfRangeException(
                nameof(windowSeconds),
                "The streaming window must contain at least two decode chunks so adjacent windows overlap.");
        if (_blockStreaming && _windowSamples < _blockSamples * 2)
            throw new ArgumentOutOfRangeException(
                nameof(windowSeconds),
                "The feature window must contain at least two streaming encoder blocks.");

        _streamWindowTokens = _blockStreaming
            ? checked((int)Math.Ceiling((double)_windowSamples / _blockSamples)
                * Qwen3Prompt.GetEncoderOutputLength(_blockSamples / WhisperMel.Hop))
            : 0;

        _decodeEveryBlocks = Math.Max(1, decodeEveryBlocks);
        _skipSilentBlocks = skipSilentBlocks;
        _textAligner = new Qwen3StreamingTextAligner(emitFirstBlock);

        if (language is not null)
            TrySetLanguage(language);
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
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0)
            return null;

        if (_blockStreaming)
            return ProcessBlockAudio(chunk);

        _audio.AddRange(chunk);
        _totalSamples += chunk.Length;
        TrimAudioWindow();

        if (_totalSamples < _initialWindowSamples
            || _totalSamples - _lastDecodedTotalSamples < _chunkSamples)
            return null;

        _lastDecodedTotalSamples = _totalSamples;
        return _textAligner.Push(
            TranscribeCore(CurrentAudioWindow()),
            windowShifted: _totalSamples > _windowSamples);
    }

    /// <summary>
    /// Provisional window hypothesis minus the committed prefix. It is not
    /// committed to the transcript: the accurate confirm-by-next-window policy
    /// still decides what becomes final, so displaying this early keeps the
    /// baseline WER while removing the multi-second wait for the first text.
    /// </summary>
    public string? PartialText =>
        _textAligner.PartialText is { Length: > 0 } provisional ? provisional : null;

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_blockStreaming)
            return FlushBlockAudio();

        if (_totalSamples == 0)
            return null;

        string? delta = null;
        if (_lastDecodedTotalSamples < _totalSamples || !_textAligner.HasPendingText)
        {
            _lastDecodedTotalSamples = _totalSamples;
            delta = _textAligner.Push(
                TranscribeCore(CurrentAudioWindow()),
                windowShifted: _totalSamples > _windowSamples);
        }

        return CombineDeltas(delta, _textAligner.Flush());
    }

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.Clear();
        _blockAudio.Clear();
        _streamFeatures.Clear();
        _textAligner.Reset();
        _totalSamples = 0;
        _lastDecodedTotalSamples = 0;
        _encodedAudioSamples = 0;
        _streamFeatureTokens = 0;
        _blocksSinceDecode = 0;
        _autoLanguageTracker.Reset();

        if (_autoLanguageDetection && !_translationEnabled)
        {
            _recognitionLanguage = null;
            RebuildPrompt();
        }
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

    /// <inheritdoc />
    public bool TrySetLanguage(string language)
    {
        if (!Qwen3Prompt.TryNormalizeLanguage(language, out var normalized))
            return false;

        bool modeChanged = _autoLanguageDetection != (normalized is null)
            || !string.Equals(_recognitionLanguage, normalized, StringComparison.OrdinalIgnoreCase);
        _autoLanguageDetection = normalized is null;
        _recognitionLanguage = normalized;
        _autoLanguageTracker.Reset();
        if (!_translationEnabled)
            RebuildPrompt();

        if (modeChanged && (_totalSamples > 0 || _streamFeatureTokens > 0 || _audio.Count > 0))
            ResetStreamingState();

        return true;
    }

    /// <inheritdoc />
    public bool TrySetTranslation(bool enabled, string targetLanguage)
    {
        string? normalized = null;
        if (enabled && (!Qwen3Prompt.TryNormalizeLanguage(targetLanguage, out normalized) || normalized is null))
            return false;

        _translationEnabled = enabled;
        _translationLanguage = enabled ? normalized : null;
        if (!enabled && _autoLanguageDetection)
        {
            _recognitionLanguage = null;
            _autoLanguageTracker.Reset();
        }
        RebuildPrompt();
        return true;
    }

    // ------------------------------------------------------------------ //
    // Inference pipeline                                                  //
    // ------------------------------------------------------------------ //

    private string TranscribeCore(float[] waveform)
    {
        var mel = WhisperMel.ComputeLogMel(waveform, out int frames);
        int audioTokens = Qwen3Prompt.GetEncoderOutputLength(frames);

        var audioFeatures = RunEncoder(mel, frames); // [1, audioTokens, 2048]
        return DecodeFeatures(audioFeatures, audioTokens);
    }

    private string DecodeFeatures(float[] audioFeatures, int audioTokens)
    {
        var promptIds = Qwen3Prompt.BuildPromptIds(
            audioTokens,
            Volatile.Read(ref _systemPromptIds),
            Volatile.Read(ref _languagePromptIds),
            out int audioOffset);

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

        int asrTextIndex = output.IndexOf(Qwen3Prompt.AsrTextTokenId);
        bool unconfirmedLanguageMarker = false;
        if (_autoLanguageDetection && !_translationEnabled && asrTextIndex >= 0)
        {
            unconfirmedLanguageMarker = ObserveAutoLanguage(
                _tokenizer.Decode(output.Take(asrTextIndex)));
        }

        if (unconfirmedLanguageMarker && _recognitionLanguage is null)
            return "";

        var transcriptTokens = asrTextIndex >= 0
            ? output.Skip(asrTextIndex + 1)
            : output;
        return _tokenizer.Decode(transcriptTokens).Trim();
    }

    private string? ProcessBlockAudio(float[] chunk)
    {
        _blockAudio.AddRange(chunk);
        _totalSamples += chunk.Length;

        string? delta = null;
        while (_blockAudio.Count >= _blockSamples)
        {
            var block = _blockAudio.GetRange(0, _blockSamples).ToArray();
            _blockAudio.RemoveRange(0, _blockSamples);
            AppendEncodedBlock(block);

            // Decoding costs far more than encoding and the decoder regenerates the
            // whole window transcript on every call, so blocks can be coalesced into
            // one decode. Coalescing only kicks in once the window is saturated:
            // early blocks are cheap and decode every time, which keeps the first
            // partial at the first block boundary. Blocks that carry no speech cannot
            // produce text and are skipped: the next speech block (or Flush) decodes
            // the full window, so nothing is lost, only deferred.
            int every = _streamFeatureTokens >= _streamWindowTokens ? _decodeEveryBlocks : 1;
            if (++_blocksSinceDecode < every)
                continue;
            if (_skipSilentBlocks && IsSilent(block))
                continue;

            _blocksSinceDecode = 0;
            delta = DecodeStreamFeatures();
        }

        return delta;
    }

    private static bool IsSilent(float[] block)
    {
        double sum = 0;
        for (int i = 0; i < block.Length; i++)
            sum += (double)block[i] * block[i];

        return Math.Sqrt(sum / block.Length) < SilenceRmsThreshold;
    }

    private string? FlushBlockAudio()
    {
        string? delta = null;
        if (_blockAudio.Count > 0)
        {
            var block = new float[_blockSamples];
            _blockAudio.CopyTo(block, 0);
            _blockAudio.Clear();
            AppendEncodedBlock(block);
            _blocksSinceDecode = 0;
            delta = DecodeStreamFeatures();
        }

        return CombineDeltas(delta, _textAligner.Flush());
    }

    private void AppendEncodedBlock(float[] block)
    {
        var mel = WhisperMel.ComputeLogMel(block, out int frames);
        int audioTokens = Qwen3Prompt.GetEncoderOutputLength(frames);
        var features = RunEncoder(mel, frames);
        _streamFeatures.AddRange(features);
        _streamFeatureTokens += audioTokens;
        _encodedAudioSamples += block.Length;

        int excessTokens = _streamFeatureTokens - _streamWindowTokens;
        if (excessTokens <= 0)
            return;

        _streamFeatures.RemoveRange(0, excessTokens * AudioHidden);
        _streamFeatureTokens -= excessTokens;
    }

    private string? DecodeStreamFeatures()
    {
        if (_streamFeatureTokens == 0)
            return null;

        var text = DecodeFeatures(_streamFeatures.ToArray(), _streamFeatureTokens);
        return _textAligner.Push(
            text,
            windowShifted: _encodedAudioSamples > _windowSamples);
    }

    private void RebuildPrompt()
    {
        var language = _translationEnabled ? _translationLanguage : _recognitionLanguage;
        var languagePromptIds = language is null
            ? null
            : _tokenizer.Encode($"language {language}<asr_text>").ToArray();
        var systemPromptIds = _translationEnabled
            ? _tokenizer.Encode($"Translate the audio into {language}.").ToArray()
            : null;

        Volatile.Write(ref _systemPromptIds, systemPromptIds);
        Volatile.Write(ref _languagePromptIds, languagePromptIds);
    }

    private bool ObserveAutoLanguage(string languagePrefix)
    {
        if (!_autoLanguageDetection
            || _translationEnabled
            || _recognitionLanguage is not null)
            return false;

        bool observed = _autoLanguageTracker.Observe(languagePrefix, out var lockedLanguage);
        if (lockedLanguage is not null)
        {
            _recognitionLanguage = lockedLanguage;
            RebuildPrompt();
        }

        return observed;
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

    /// <summary>Looks up one token embedding row, widening the stored fp16 row to fp32.</summary>
    private float[] EmbedToken(int token)
    {
        if ((uint)token >= VocabSize)
            throw new InvalidDataException($"Qwen3 token id is outside the vocabulary: {token}");

        var row = new float[AudioHidden];
        var source = _embedTokens.AsSpan(token * AudioHidden, AudioHidden);
        for (int i = 0; i < row.Length; i++)
            row[i] = (float)source[i];
        return row;
    }

    private float[] CurrentAudioWindow()
    {
        int start = Math.Max(0, _audio.Count - _windowSamples);
        return _audio.GetRange(start, _audio.Count - start).ToArray();
    }

    private void TrimAudioWindow()
    {
        int removable = _audio.Count - _windowSamples;
        if (removable > 0)
            _audio.RemoveRange(0, removable);
    }

    private static int SecondsToSamples(double seconds, string parameterName)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Seconds must be finite and greater than zero.");

        var samples = seconds * WhisperMel.SampleRate;
        if (samples > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "The requested audio window is too large.");

        return Math.Max(1, (int)Math.Round(samples));
    }

    private static string? CombineDeltas(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first)) return second;
        if (string.IsNullOrEmpty(second)) return first;
        return first + second;
    }

    // ------------------------------------------------------------------ //
    // Setup                                                               //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Reads the fp16 token embedding matrix as stored on disk.
    /// </summary>
    /// <remarks>
    /// The table stays in fp16 (594 MB) and each lookup widens the one row it needs
    /// (2048 values, microseconds). Widening the whole matrix up front cost 637 ms of
    /// startup, peaked at 1.78 GB of allocations and left 1.19 GB resident for the
    /// lifetime of the recognizer, for a lookup that only ever reads ~9 rows per decode.
    /// </remarks>
    private static Half[] LoadEmbedTokens(string path)
    {
        const int expectedValues = VocabSize * AudioHidden;
        long length = new FileInfo(path).Length;
        if (length != (long)expectedValues * sizeof(ushort))
        {
            throw new InvalidDataException(
                $"Embedding table has {length} bytes, expected {expectedValues * sizeof(ushort)} " +
                $"({VocabSize} x {AudioHidden} float16).");
        }

        // Read straight into the Half[]: going through File.ReadAllBytes added a
        // second 594 MB buffer that served no purpose.
        var halves = new Half[expectedValues];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(MemoryMarshal.AsBytes(halves.AsSpan()));
        return halves;
    }

    private static SessionOptions CreateSessionOptions(string executionProvider, int intraOpThreads = 0)
    {
        int threads = intraOpThreads > 0
            ? intraOpThreads
            : Math.Max(2, Environment.ProcessorCount / 2);
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        // ONNX Runtime's CPU workers spin-wait by default, which starves the other
        // sessions and the host threads; see OrtCpuTuning for the measurements.
        OrtCpuTuning.DisableThreadSpinning(options);

        // A persisted provider setting may name a provider that is no longer
        // shipped (for example "cuda" after a GPU build); degrade to CPU
        // instead of failing session creation.
        ExecutionProviderSelector.Apply(options, executionProvider);

        return options;
    }

    /// <summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
        _decoderInit.Dispose();
        _decoderStep.Dispose();
    }
}
