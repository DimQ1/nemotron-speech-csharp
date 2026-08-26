using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpeechLib.Audio;

/// <summary>
/// Streaming voice-activity detector backed by Silero VAD (silero_vad.onnx).
/// Model-agnostic: it inspects raw 16 kHz mono audio and reports speech
/// probability per 512-sample window, so it can gate any recognizer.
/// </summary>
public sealed class SileroVadFilter : IVadFilter
{
    /// <summary>Silero hop size at 16 kHz (32 ms of new audio per step).</summary>
    public const int HopSamples = 512;

    /// <summary>Left context prepended to each hop (matches onnx-asr silero.py).</summary>
    public const int ContextSamples = 64;

    /// <summary>Model input width = context + hop (576 at 16 kHz).</summary>
    public const int WindowSamples = ContextSamples + HopSamples;

    private const int StateSize = 2 * 1 * 128;

    private readonly InferenceSession _session;
    private readonly float[] _state = new float[StateSize];
    private readonly float[] _context = new float[ContextSamples];
    private readonly List<float> _pending = new();
    private bool _disposed;

    /// <param name="modelPath">Path to silero_vad.onnx.</param>
    /// <param name="threshold">Speech probability threshold (0..1).</param>
    public SileroVadFilter(string modelPath, float threshold = 0.5f)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Silero VAD model not found: {modelPath}", modelPath);

        Threshold = threshold;
        _session = new InferenceSession(modelPath, CreateOptions());
    }

    /// <summary>Speech probability threshold (0..1). Higher = stricter.</summary>
    public float Threshold { get; set; }

    /// <summary>Speech probability of the most recently scored window (0..1).</summary>
    public float LastProbability { get; private set; }

    /// <summary>
    /// Feed an audio batch. Scores every complete 512-sample window and returns
    /// true if any window contains speech at or above <see cref="Threshold"/>.
    /// Leftover samples are carried into the next call.
    /// </summary>
    public bool HasSpeech(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < samples.Length; i++)
            _pending.Add(samples[i]);

        bool speech = false;
        float best = 0f;
        while (_pending.Count >= HopSamples)
        {
            var hop = _pending.GetRange(0, HopSamples).ToArray();
            _pending.RemoveRange(0, HopSamples);
            float p = ScoreWindow(hop);
            if (p > best) best = p;
            if (p >= Threshold) speech = true;
        }

        LastProbability = best;
        return speech;
    }

    /// <summary>Score one hop (512 samples) with the 64-sample left context. Returns speech probability 0..1.</summary>
    private float ScoreWindow(float[] hop)
    {
        // input = [context(64) | hop(512)] — context from the previous step.
        var window = new float[WindowSamples];
        Array.Copy(_context, 0, window, 0, ContextSamples);
        Array.Copy(hop, 0, window, ContextSamples, HopSamples);

        // Slide the context to the tail of the current hop for the next step.
        Array.Copy(hop, HopSamples - ContextSamples, _context, 0, ContextSamples);

        var input = new DenseTensor<float>(window, new[] { 1, WindowSamples });
        var state = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        var sr = new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>());

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("state", state),
            NamedOnnxValue.CreateFromTensor("sr", sr),
        };

        using var results = _session.Run(inputs);
        float prob = results[0].AsTensor<float>()[0, 0];

        // Carry recurrent state forward (output stateN has shape [2,1,128]).
        var nextState = results[1].AsTensor<float>().ToArray();
        Array.Copy(nextState, _state, Math.Min(nextState.Length, _state.Length));

        return prob;
    }

    /// <summary>Reset the recurrent state and any buffered samples (new utterance).</summary>
    public void Reset()
    {
        Array.Clear(_state, 0, _state.Length);
        Array.Clear(_context, 0, _context.Length);
        _pending.Clear();
        LastProbability = 0f;
    }

    private static SessionOptions CreateOptions()
        => new()
        {
            // VAD is tiny and latency-sensitive; a couple of threads is plenty.
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
