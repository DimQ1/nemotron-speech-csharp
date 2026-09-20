using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpeechLib.ParakeetTdt;

/// <summary>
/// Parakeet TDT 0.6B v3 ASR recognizer on plain ONNX Runtime.
///
/// Executes the onnx-asr exported artifacts (inside a quantization folder
/// fp32/ | int8/ | int4/):
///   - nemo128.onnx                  (log-mel preprocessor: waveform -> [1,128,T])
///   - encoder-model.onnx            (FastConformer encoder)
///   - decoder_joint-model.onnx      (TDT decoder + joint: token + duration)
///   - vocab.txt + config.json
///
/// The TDT greedy decode loop is ported from onnx-asr's
/// <c>NemoConformerTdt</c> / <c>_AsrWithTransducerDecoding</c>.
///
/// Streaming: the model uses "regular" full attention, so streaming follows
/// NeMo's buffer-based approach — overlapping windows of
/// [left-context | chunk | right-context] audio are encoded, and only the
/// frames belonging to the chunk are decoded (TDT decoder state is carried
/// across chunks). Audio is buffered; a chunk is decoded from
/// <see cref="ProcessAudio"/> once chunk + right seconds are available, and
/// the tail is decoded in <see cref="Flush"/>.
/// </summary>
public sealed class ParakeetTdtRecognizer : IStreamingSpeechRecognizer, IUtteranceStreamingRecognizer
{
    private static readonly Regex DecodeSpacePattern = new(@"\A\s|\s\B|(\s)\b", RegexOptions.Compiled);

    /// <summary>Audio samples per encoder frame (160 samples/mel frame × 8 subsampling).</summary>
    private const int SamplesPerFrame = 1280;

    private readonly InferenceSession _preprocessor;   // nemo128.onnx
    private readonly InferenceSession _encoder;        // encoder-model.int8.onnx
    private readonly InferenceSession _decoderJoint;   // decoder_joint-model.int8.onnx

    private readonly Dictionary<int, string> _vocab = new();
    private readonly HashSet<int> _wordStartIds = new();
    private readonly int _vocabSize;
    private readonly int _blankIdx;
    private readonly int _maxTokensPerStep;

    private readonly List<float> _audio = new();

    // Decoded tokens waiting for the word they belong to to finish (streaming output
    // must not commit half a word at a chunk boundary).
    private readonly List<int> _heldIds = new();
    private readonly int _chunkSamples;
    private readonly int _leftSamples;
    private readonly int _rightSamples;
    private readonly int _stopEouSamples;

    // TDT decoder state carried across chunks (streaming continuity).
    private DenseTensor<float> _state1 = new(new[] { 2, 1, 640 });
    private DenseTensor<float> _state2 = new(new[] { 2, 1, 640 });
    private int _lastToken;
    private int _decodedSamples;
    private bool _emittedAnyText;
    private bool _disposed;

    // Blank-based endpointing + partial/final output.
    private long _lastEmitSample = -1;                    // absolute sample of the last emitted token
    private readonly List<int> _eouSplits = new();        // token indices that start a new utterance
    private readonly StringBuilder _partial = new();      // uncommitted text since the last EoU
    private readonly StringBuilder _pendingFinal = new(); // utterances finalized during the current step

    /// <inheritdoc />
    public int SampleRate => 16000;

    /// <inheritdoc />
    public int ChunkSamples => 1600; // 100 ms at 16 kHz

    /// <inheritdoc />
    public double StopHistoryEouSeconds { get; }

    /// <summary>
    /// Loads a quantization folder (fp32 / int8 / int4) of the exported model.
    /// The folder must contain encoder-model.onnx, decoder_joint-model.onnx,
    /// nemo128.onnx, vocab.txt and config.json (standard onnx-asr names, no
    /// quantization suffix — the folder itself selects the precision).
    /// </summary>
    /// <param name="modelDir">Path to the quantization folder (e.g. .../int8).</param>
    /// <param name="chunkSeconds">Chunk length decoded per call (seconds).</param>
    /// <param name="leftContextSeconds">Left audio context prepended to each chunk.</param>
    /// <param name="rightContextSeconds">Right audio context appended to each chunk.</param>
    /// <param name="stopHistoryEouSeconds">Silence (seconds of consecutive blank frames)
    /// that closes an utterance for blank-based endpointing.</param>
    /// <param name="executionProvider">Requested provider: "cpu", "cuda" or "dml".
    /// Falls back to CPU when the requested provider is unavailable.</param>
    public ParakeetTdtRecognizer(
        string modelDir,
        double chunkSeconds = 2.0,
        double leftContextSeconds = 5.0,
        double rightContextSeconds = 2.0,
        double stopHistoryEouSeconds = 0.8,
        string executionProvider = "cpu")
    {
        var dir = Path.GetFullPath(modelDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Model directory not found: {dir}");

        // Constrain ORT threads so the heavy full-window encoder does not saturate
        // every core on each chunk (see DecodeNextChunk: a 14s window is re-encoded
        // every 2s of audio). Half the logical cores is plenty for real-time on CPU.
        // The execution provider (cpu/cuda/dml) is selected at runtime and falls
        // back to CPU when the requested provider's native DLL is not present.
        var options = CreateSessionOptions(executionProvider);
        _preprocessor = new InferenceSession(Path.Combine(dir, "nemo128.onnx"), options);
        _encoder = new InferenceSession(Path.Combine(dir, "encoder-model.onnx"), options);
        _decoderJoint = new InferenceSession(Path.Combine(dir, "decoder_joint-model.onnx"), options);

        LoadVocab(Path.Combine(dir, "vocab.txt"));
        _vocabSize = _vocab.Count;
        _blankIdx = _vocab.First(kv => kv.Value == "<blk>").Key;

        _maxTokensPerStep = LoadMaxTokensPerStep(Path.Combine(dir, "config.json"));
        _chunkSamples = (int)(16000 * chunkSeconds);
        _leftSamples = (int)(16000 * leftContextSeconds);
        _rightSamples = (int)(16000 * rightContextSeconds);
        _stopEouSamples = (int)(16000 * stopHistoryEouSeconds);
        StopHistoryEouSeconds = stopHistoryEouSeconds;
        _lastToken = _blankIdx;
    }

    /// <inheritdoc />
    public string? ProcessAudio(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.AddRange(chunk);

        int available = _audio.Count - _decodedSamples;
        return available >= _chunkSamples + _rightSamples
            ? DetokenizeCompleteWords(DecodeNextChunkIds(), flush: false)
            : null;
    }

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _decodedSamples < _audio.Count
            ? DetokenizeCompleteWords(DecodeRemainingIds(), flush: true)
            : DetokenizeCompleteWords([], flush: true);
    }

    /// <inheritdoc />
    public StreamingResult ProcessUtterance(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.AddRange(chunk);

        int available = _audio.Count - _decodedSamples;
        if (available >= _chunkSamples + _rightSamples)
            AppendUtterances(DecodeNextChunkIds());

        return TakeResult();
    }

    /// <inheritdoc />
    public StreamingResult FlushUtterance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decodedSamples < _audio.Count)
            AppendUtterances(DecodeRemainingIds());

        FinalizeCurrentPartial();
        return TakeResult();
    }

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.Clear();
        _decodedSamples = 0;
        _state1 = new DenseTensor<float>(new[] { 2, 1, 640 });
        _state2 = new DenseTensor<float>(new[] { 2, 1, 640 });
        _lastToken = _blankIdx;
        _emittedAnyText = false;
        _lastEmitSample = -1;
        _eouSplits.Clear();
        _heldIds.Clear();
        _partial.Clear();
        _pendingFinal.Clear();
    }

    /// <summary>Transcribe a complete 16 kHz mono float waveform (fresh decoder state).</summary>
    public string Transcribe(float[] waveform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var encodings = Encode(waveform);
        _state1 = new DenseTensor<float>(new[] { 2, 1, 640 });
        _state2 = new DenseTensor<float>(new[] { 2, 1, 640 });
        _lastToken = _blankIdx;
        _lastEmitSample = -1;
        _heldIds.Clear();
        var ids = DecodeFrames(encodings, 0, encodings.Length, 0);
        return Detokenize(ids);
    }

    // ------------------------------------------------------------------ //
    // Buffer-based streaming                                               //
    // ------------------------------------------------------------------ //

    private List<int> DecodeNextChunkIds()
    {
        int windowStart = Math.Max(0, _decodedSamples - _leftSamples);
        int windowEnd = Math.Min(_audio.Count, _decodedSamples + _chunkSamples + _rightSamples);
        if (windowEnd <= windowStart) return new List<int>();

        var window = _audio.GetRange(windowStart, windowEnd - windowStart).ToArray();
        var encodings = Encode(window);

        int leftFrames = (_decodedSamples - windowStart) / SamplesPerFrame;
        int chunkFrames = _chunkSamples / SamplesPerFrame;
        int chunkEnd = Math.Min(encodings.Length, leftFrames + chunkFrames);

        var ids = DecodeFrames(encodings, leftFrames, chunkEnd, windowStart);
        _decodedSamples += _chunkSamples;
        TrimConsumedAudio();
        return ids;
    }

    private List<int> DecodeRemainingIds()
    {
        int windowStart = Math.Max(0, _decodedSamples - _leftSamples);
        int windowEnd = _audio.Count;
        if (windowEnd <= windowStart) return new List<int>();

        var window = _audio.GetRange(windowStart, windowEnd - windowStart).ToArray();
        var encodings = Encode(window);

        int leftFrames = (_decodedSamples - windowStart) / SamplesPerFrame;
        var ids = DecodeFrames(encodings, leftFrames, encodings.Length, windowStart);
        _decodedSamples = _audio.Count;
        return ids;
    }

    /// <summary>
    /// Splits a chunk's token ids at blank-detected end-of-utterance boundaries,
    /// appending continuation text to <see cref="_partial"/> and committing
    /// completed utterances to <see cref="_pendingFinal"/>.
    /// </summary>
    private void AppendUtterances(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return;

        // A blank run may span the chunk boundary: if the first token of this
        // chunk already starts a new utterance, close the current partial first.
        if (_eouSplits.Count > 0 && _eouSplits[0] == 0)
            FinalizeCurrentPartial();

        int segmentStart = 0;
        int splitIndex = 0;
        while (segmentStart < ids.Count)
        {
            int boundary = splitIndex < _eouSplits.Count ? _eouSplits[splitIndex] : ids.Count;
            if (boundary <= segmentStart)
            {
                splitIndex++;
                continue;
            }

            if (segmentStart > 0)
                FinalizeCurrentPartial();

            AppendSegment(ids, segmentStart, boundary);
            segmentStart = boundary;
            if (boundary < ids.Count)
                splitIndex++;
        }
    }

    private void AppendSegment(IReadOnlyList<int> ids, int start, int end)
    {
        var segment = ids.Skip(start).Take(end - start).ToList();
        var text = Detokenize(segment);
        if (text.Length == 0) return;

        bool startsWord = _wordStartIds.Contains(segment[0]);
        bool needSpace = _partial.Length > 0 && startsWord;
        if (needSpace) _partial.Append(' ');
        _partial.Append(text);
    }

    /// <summary>Commits the current partial as a finalized utterance.</summary>
    private void FinalizeCurrentPartial()
    {
        if (_partial.Length == 0) return;
        if (_pendingFinal.Length > 0) _pendingFinal.Append(' ');
        _pendingFinal.Append(_partial);
        _partial.Clear();
    }

    private StreamingResult TakeResult()
    {
        var final = _pendingFinal.Length > 0 ? _pendingFinal.ToString() : null;
        _pendingFinal.Clear();
        return new StreamingResult(_partial.ToString(), final);
    }

    /// <summary>
    /// Emits only the decoded ids that form complete words and holds the trailing word
    /// back until a later chunk (or <see cref="Flush"/>) confirms it.
    /// </summary>
    /// <remarks>
    /// Chunk boundaries fall wherever the audio happens to be, so committing the raw
    /// chunk detokenization split words in half: a trace showed "disappro" followed by
    /// "ved". Holding the growing word changes only *when* text appears, never the final
    /// transcript. An end-of-utterance split (a blank gap) proves everything before it is
    /// complete, so that part is released immediately.
    /// </remarks>
    private string? DetokenizeCompleteWords(IReadOnlyList<int> ids, bool flush)
    {
        int heldBefore = _heldIds.Count;
        _heldIds.AddRange(ids);

        int releaseEnd;
        if (flush)
        {
            releaseEnd = _heldIds.Count;
        }
        else
        {
            int lastSplit = _eouSplits.Count > 0 ? heldBefore + _eouSplits[^1] : -1;
            int lastWordStart = -1;
            for (int i = _heldIds.Count - 1; i > 0; i--)
            {
                if (_wordStartIds.Contains(_heldIds[i]))
                {
                    lastWordStart = i;
                    break;
                }
            }

            releaseEnd = lastSplit >= lastWordStart ? _heldIds.Count : lastWordStart;
        }

        if (releaseEnd <= 0)
            return null;

        var complete = _heldIds.GetRange(0, releaseEnd);
        _heldIds.RemoveRange(0, releaseEnd);
        return DetokenizeChunk(complete);
    }

    /// <summary>
    /// Detokenizes a chunk's ids and decides whether it needs a leading space
    /// when concatenated with the previous delta. A space is added only when the
    /// chunk begins a NEW word (its first token carries the SentencePiece ▁
    /// marker) AND text was already emitted; continuation tokens and punctuation
    /// join the previous word without a space. This prevents both mid-word
    /// splits ("достаточ ный") and sentence run-ons ("три.Слышал").
    /// </summary>
    private string DetokenizeChunk(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return "";

        var text = Detokenize(ids);
        if (text.Length == 0) return "";

        bool startsWord = _wordStartIds.Contains(ids[0]);
        bool first = !_emittedAnyText;
        _emittedAnyText = true;

        return ShouldPrefixSpace(startsWord, !first) ? " " + text : text;
    }

    /// <summary>True when a chunk delta needs a leading space: it starts a new word and
    /// some text was already emitted (no leading space on the very first word).</summary>
    private static bool ShouldPrefixSpace(bool startsWord, bool alreadyEmitted)
        => startsWord && alreadyEmitted;

    // ------------------------------------------------------------------ //
    // Inference pipeline                                                  //
    // ------------------------------------------------------------------ //

    private float[][] Encode(float[] waveform)
    {
        // 1) log-mel features: waveforms [1,N] -> features [1,128,T], features_lens [1]
        var (features, featuresLens) = RunPreprocessor(waveform);

        // 2) encoder: audio_signal [1,128,T] -> encodings [T_enc, 1024]
        var (encodings, _) = RunEncoder(features, featuresLens);
        return encodings;
    }

    /// <summary>
    /// TDT greedy decode over encoder frames [startFrame, endFrame), carrying
    /// the decoder state (_state1/_state2/_lastToken) across calls.
    /// </summary>
    private List<int> DecodeFrames(float[][] encodings, int startFrame, int endFrame, int windowStartSample)
    {
        _eouSplits.Clear();
        var tokens = new List<int>();
        int t = startFrame;
        int emitted = 0;

        while (t < endFrame)
        {
            var (logits, nextState1, nextState2) =
                RunDecoderJoint(encodings, t, _lastToken, _state1, _state2);

            // logits = [vocab (vocabSize)] + [duration (decoderDim - vocabSize)]
            int token = ArgMax(logits, 0, _vocabSize);
            int duration = ArgMax(logits, _vocabSize, logits.Length - _vocabSize);

            if (token != _blankIdx)
            {
                // Blank-based endpointing: the silence between two emitted tokens
                // is the run of blank frames between them. A gap longer than
                // _stopEouSamples marks the start of a new utterance.
                long absSample = windowStartSample + (long)t * SamplesPerFrame;
                if (_lastEmitSample >= 0 && absSample - _lastEmitSample > _stopEouSamples)
                    _eouSplits.Add(tokens.Count);
                _lastEmitSample = absSample;

                tokens.Add(token);
                _state1 = nextState1;
                _state2 = nextState2;
                _lastToken = token;
                emitted++;
            }

            if (duration > 0)
            {
                t += duration;
                emitted = 0;
            }
            else if (token == _blankIdx || emitted >= _maxTokensPerStep)
            {
                // onnx-asr: advance only on blank or when the per-frame token cap is
                // reached; a non-blank token with zero duration stays on the SAME frame
                // (re-decoded with updated state) so co-located tokens are not dropped.
                t += 1;
                emitted = 0;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Drop fully-consumed audio from the head of the buffer once the kept tail
    /// exceeds twice the left context. Prevents unbounded growth of _audio (and the
    /// GetRange copies in every chunk) during long sessions.
    /// </summary>
    private void TrimConsumedAudio()
    {
        int removable = _decodedSamples - _leftSamples;
        if (removable > _leftSamples)
        {
            _audio.RemoveRange(0, removable);
            _decodedSamples -= removable;
        }
    }

    private static SessionOptions CreateSessionOptions(string executionProvider)
    {
        // The encoder re-encodes the whole left+chunk+right window on every chunk and is
        // compute-bound, so it uses every core. It used to be pinned to half the cores to
        // stop the heavy window from saturating the machine, but that was a symptom of
        // ONNX Runtime's worker spinning, which is now disabled (see OrtCpuTuning):
        // measured on 20 logical cores, streaming RTF improved from 0.074 at half the
        // cores to 0.067 at all of them, with an identical transcript.
        int threads = Math.Max(2, Environment.ProcessorCount);
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        OrtCpuTuning.DisableThreadSpinning(options);

        // Runtime provider selection with graceful CPU fallback. The provider
        // is picked from the libraries actually shipped.
        ExecutionProviderSelector.Apply(options, executionProvider);

        return options;
    }

    private (float[] features, long featuresLens) RunPreprocessor(float[] waveform)
    {
        var waveformTensor = new DenseTensor<float>(waveform, new[] { 1, waveform.Length });
        var lensTensor = new DenseTensor<long>(new[] { (long)waveform.Length }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("waveforms", waveformTensor),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", lensTensor),
        };

        using var results = _preprocessor.Run(inputs);
        var features = results[0].AsTensor<float>();
        var featuresLens = results[1].AsTensor<long>();

        return (features.ToArray(), featuresLens[0]);
    }

    private (float[][] encodings, int encLen) RunEncoder(float[] features, long featuresLens)
    {
        // nemo128 produced features [1, 128, T]
        int t = (int)(features.LongLength / 128);
        var audioTensor = new DenseTensor<float>(features, new[] { 1, 128, t });
        var lenTensor = new DenseTensor<long>(new[] { featuresLens }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", audioTensor),
            NamedOnnxValue.CreateFromTensor("length", lenTensor),
        };

        using var results = _encoder.Run(inputs);
        var outputs = results[0].AsTensor<float>();        // [1, 1024, T_enc]
        var encLens = results[1].AsTensor<long>();

        int d = 1024;
        int tEnc = (int)encLens[0];
        int totalFrames = (int)(outputs.Length / d);       // batch(1) * T_enc

        // Transpose [1, 1024, T_enc] -> [T_enc, 1024] (row per frame)
        var encodings = new float[totalFrames][];
        for (int frame = 0; frame < totalFrames; frame++)
        {
            var row = new float[d];
            for (int dim = 0; dim < d; dim++)
                row[dim] = outputs[0, dim, frame];
            encodings[frame] = row;
        }

        return (encodings, tEnc);
    }

    private (float[] logits, DenseTensor<float> state1, DenseTensor<float> state2) RunDecoderJoint(
        float[][] encodings, int t, int lastToken, DenseTensor<float> state1, DenseTensor<float> state2)
    {
        // encoder_outputs [1, 1024, 1] — single frame
        int d = 1024;
        var encoderOutputs = new DenseTensor<float>(new[] { 1, d, 1 });
        for (int dim = 0; dim < d; dim++)
            encoderOutputs[0, dim, 0] = encodings[t][dim];

        var targets = new DenseTensor<int>(new[] { 1, 1 });
        targets[0, 0] = lastToken;
        var targetLength = new DenseTensor<int>(new[] { 1 });
        targetLength[0] = 1;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("encoder_outputs", encoderOutputs),
            NamedOnnxValue.CreateFromTensor("targets", targets),
            NamedOnnxValue.CreateFromTensor("target_length", targetLength),
            NamedOnnxValue.CreateFromTensor("input_states_1", state1),
            NamedOnnxValue.CreateFromTensor("input_states_2", state2),
        };

        using var results = _decoderJoint.Run(inputs);
        var logits = results[0].AsTensor<float>();          // [1, 1, vocab + duration]
        var outState1 = results[2].AsTensor<float>();       // [2, 1, 640]
        var outState2 = results[3].AsTensor<float>();       // [2, 1, 640]

        var flat = logits.ToArray();
        var next1 = new DenseTensor<float>(outState1.ToArray(), new[] { 2, 1, 640 });
        var next2 = new DenseTensor<float>(outState2.ToArray(), new[] { 2, 1, 640 });

        return (flat, next1, next2);
    }

    // ------------------------------------------------------------------ //
    // Vocab / decoding                                                    //
    // ------------------------------------------------------------------ //

    private void LoadVocab(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int sep = line.LastIndexOf(' ');
            if (sep <= 0) continue;
            if (!int.TryParse(line[(sep + 1)..], out int id)) continue;

            var raw = line[..sep];
            if (raw.StartsWith('\u2581'))
                _wordStartIds.Add(id);
            _vocab[id] = raw.Replace("\u2581", " "); // ▁ -> space
        }
    }

    private static int LoadMaxTokensPerStep(string path)
    {
        if (!File.Exists(path)) return 10;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("max_tokens_per_step", out var v) && v.TryGetInt32(out int n))
            return n;
        return 10;
    }

    private string Detokenize(IReadOnlyList<int> ids)
    {
        var sb = new StringBuilder(ids.Count * 4);
        foreach (var id in ids)
            if (_vocab.TryGetValue(id, out var token))
                sb.Append(token);
        // Port of onnx-asr DECODE_SPACE_PATTERN: \A\s|\s\B|(\s)\b
        return DecodeSpacePattern.Replace(sb.ToString(), m => m.Groups[1].Success ? " " : "");
    }

    private static int ArgMax(float[] values, int offset, int count)
    {
        int best = 0;
        float bestVal = float.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            float v = values[offset + i];
            if (v > bestVal)
            {
                bestVal = v;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// True when <paramref name="modelDir"/> contains a parakeet-tdt ONNX export
    /// (<c>config.json</c> with <c>model_type: "nemo-conformer-tdt"</c>), as
    /// opposed to a Nemotron GenAI export (<c>genai_config.json</c>).
    /// </summary>
    public static bool IsParakeetTdtModel(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir)) return false;
        var config = Path.Combine(modelDir, "config.json");
        if (!File.Exists(config)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
            return doc.RootElement.TryGetProperty("model_type", out var t)
                && t.GetString() == "nemo-conformer-tdt";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preprocessor.Dispose();
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
}
