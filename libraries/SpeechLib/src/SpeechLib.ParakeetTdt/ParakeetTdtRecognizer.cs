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
/// Streaming: the exported encoder is non-cache-aware (full-length), so this
/// implementation transcribes incrementally in fixed-length segments: audio is
/// buffered and, once <c>segmentSeconds</c> of audio has accumulated, that
/// segment is decoded and returned from <see cref="ProcessAudio"/>. The
/// remaining tail is decoded in <see cref="Flush"/>. (A fully incremental
/// cache-aware encoder is a separate export step — see the converter README.)
/// </summary>
public sealed class ParakeetTdtRecognizer : IStreamingSpeechRecognizer
{
    private static readonly Regex DecodeSpacePattern = new(@"\A\s|\s\B|(\s)\b", RegexOptions.Compiled);

    private readonly InferenceSession _preprocessor;   // nemo128.onnx
    private readonly InferenceSession _encoder;        // encoder-model.int8.onnx
    private readonly InferenceSession _decoderJoint;   // decoder_joint-model.int8.onnx

    private readonly Dictionary<int, string> _vocab = new();
    private readonly int _vocabSize;
    private readonly int _blankIdx;
    private readonly int _maxTokensPerStep;

    private readonly List<float> _buffer = new();
    private readonly int _segmentSamples;
    private bool _disposed;

    /// <inheritdoc />
    public int SampleRate => 16000;

    /// <inheritdoc />
    public int ChunkSamples => 1600; // 100 ms at 16 kHz

    /// <summary>
    /// Loads a quantization folder (fp32 / int8 / int4) of the exported model.
    /// The folder must contain encoder-model.onnx, decoder_joint-model.onnx,
    /// nemo128.onnx, vocab.txt and config.json (standard onnx-asr names, no
    /// quantization suffix — the folder itself selects the precision).
    /// </summary>
    /// <param name="modelDir">Path to the quantization folder (e.g. .../int8).</param>
    /// <param name="segmentSeconds">Streaming segment length (seconds) decoded per call.</param>
    public ParakeetTdtRecognizer(string modelDir, double segmentSeconds = 2.0)
    {
        var dir = Path.GetFullPath(modelDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Model directory not found: {dir}");

        _preprocessor = new InferenceSession(Path.Combine(dir, "nemo128.onnx"));
        _encoder = new InferenceSession(Path.Combine(dir, "encoder-model.onnx"));
        _decoderJoint = new InferenceSession(Path.Combine(dir, "decoder_joint-model.onnx"));

        LoadVocab(Path.Combine(dir, "vocab.txt"));
        _vocabSize = _vocab.Count;
        _blankIdx = _vocab.First(kv => kv.Value == "<blk>").Key;

        _maxTokensPerStep = LoadMaxTokensPerStep(Path.Combine(dir, "config.json"));
        _segmentSamples = Math.Max(ChunkSamples, (int)(16000 * segmentSeconds));
    }

    /// <inheritdoc />
    public string? ProcessAudio(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _buffer.AddRange(chunk);
        return _buffer.Count >= _segmentSamples ? TranscribeBuffer() : null;
    }

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _buffer.Count > 0 ? TranscribeBuffer() : null;
    }

    private string? TranscribeBuffer()
    {
        var waveform = _buffer.ToArray();
        _buffer.Clear();
        return Transcribe(waveform);
    }

    /// <summary>Transcribe a complete 16 kHz mono float waveform.</summary>
    public string Transcribe(float[] waveform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ids = GreedyDecode(waveform);
        return Detokenize(ids);
    }

    // ------------------------------------------------------------------ //
    // Inference pipeline                                                  //
    // ------------------------------------------------------------------ //

    private IReadOnlyList<int> GreedyDecode(float[] waveform)
    {
        // 1) log-mel features: waveforms [1,N] -> features [1,128,T], features_lens [1]
        var (features, featuresLens) = RunPreprocessor(waveform);

        // 2) encoder: audio_signal [1,128,T] -> outputs [1,1024,T_enc], encoded_lengths [1]
        var (encodings, encLen) = RunEncoder(features, featuresLens);

        // 3) TDT greedy decode over encoder frames
        var state1 = new DenseTensor<float>(new[] { 2, 1, 640 });
        var state2 = new DenseTensor<float>(new[] { 2, 1, 640 });

        var tokens = new List<int>();
        int t = 0;
        int emitted = 0;

        while (t < encLen)
        {
            var lastToken = tokens.Count > 0 ? tokens[^1] : _blankIdx;

            var (logits, nextState1, nextState2) = RunDecoderJoint(encodings, t, lastToken, state1, state2);

            // logits = [vocab (vocabSize)] + [duration (decoderDim - vocabSize)]
            int token = ArgMax(logits, 0, _vocabSize);
            int duration = ArgMax(logits, _vocabSize, logits.Length - _vocabSize);

            if (token != _blankIdx)
            {
                tokens.Add(token);
                state1 = nextState1;
                state2 = nextState2;
                emitted++;
            }

            if (duration > 0)
            {
                t += duration;
                emitted = 0;
            }
            else
            {
                // onnx-asr advances on blank or when max tokens per step reached;
                // a zero duration with an emitted token would stall, so advance anyway.
                t += 1;
                emitted = 0;
            }
        }

        return tokens;
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
            var token = line[..sep].Replace("\u2581", " "); // ▁ -> space
            if (!int.TryParse(line[(sep + 1)..], out int id)) continue;
            _vocab[id] = token;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preprocessor.Dispose();
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
}
