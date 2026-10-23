using Microsoft.ML.OnnxRuntime;
using SpeechLib.Audio;

namespace SpeechLib.Nemotron;

/// <summary>Decides whether a whole chunk of audio contains speech; the input of <see cref="SilenceDropRule"/>.</summary>
internal interface IChunkSpeechGate : IDisposable
{
    /// <summary>True when the chunk contains speech.</summary>
    bool ContainsSpeech(ReadOnlySpan<float> chunk);

    /// <summary>Forgets recurrent state (start of a new stream).</summary>
    void Reset();
}

/// <summary>
/// The silence drop rule of GenAI 0.16.0's streaming processor (<c>StreamingProcessor::ShouldDropChunk</c>): count
/// consecutive chunks without speech and drop a silent chunk once the count reaches
/// <c>max(prefix_padding_chunks, silence_duration_chunks)</c>. The first silent chunks are kept so the model gets the
/// pause as context before and after speech; a chunk with speech resets the count.
/// </summary>
/// <remarks>
/// A dropped chunk is not encoded: the mel overlap, the mel cache, the encoder caches and the decoder state never see
/// it. For the 560 ms chunk that is the 6th consecutive silent chunk onward (3360 ms / 560 ms); for the 1120 ms
/// chunk the 3rd. The shipped config values give an exact division, the count is computed in float like upstream.
/// </remarks>
internal sealed class SilenceDropRule
{
    private readonly IChunkSpeechGate _gate;

    public SilenceDropRule(IChunkSpeechGate gate, int silenceDurationChunks, int prefixPaddingChunks)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        SilenceDurationChunks = Math.Max(1, silenceDurationChunks);
        PrefixPaddingChunks = Math.Max(1, prefixPaddingChunks);
    }

    public int SilenceDurationChunks { get; }
    public int PrefixPaddingChunks { get; }

    /// <summary>Number of consecutive silent chunks that makes the next silent chunk droppable.</summary>
    public int DropThreshold => Math.Max(PrefixPaddingChunks, SilenceDurationChunks);

    /// <summary>Consecutive chunks without speech seen so far.</summary>
    public int ConsecutiveSilentChunks { get; private set; }

    /// <summary>True when <paramref name="chunk"/> must be dropped. Always scores the chunk (the gate has state).</summary>
    public bool ShouldDrop(ReadOnlySpan<float> chunk)
    {
        if (_gate.ContainsSpeech(chunk))
        {
            ConsecutiveSilentChunks = 0;
            return false;
        }

        ConsecutiveSilentChunks++;
        return ConsecutiveSilentChunks >= DropThreshold;
    }

    /// <summary>Clears the count and the gate's recurrent state.</summary>
    public void Reset()
    {
        ConsecutiveSilentChunks = 0;
        _gate.Reset();
    }

    /// <summary>
    /// Converts the config's durations to chunk counts exactly like upstream
    /// (<c>max(1, (int)(ms / chunk_duration_ms))</c> with a float chunk duration).
    /// </summary>
    public static (int SilenceDurationChunks, int PrefixPaddingChunks) ChunkCounts(NemotronVadConfig vad, int chunkSamples, int sampleRate)
    {
        var chunkDurationMs = (float)chunkSamples / sampleRate * 1000.0f;
        return (Math.Max(1, (int)(vad.SilenceDurationMs / chunkDurationMs)),
                Math.Max(1, (int)(vad.PrefixPaddingMs / chunkDurationMs)));
    }
}

/// <summary>
/// Adapter that lets an <see cref="IVadFilter"/> (for example the shared <see cref="SileroVadFilter"/>) act as the
/// chunk gate. The filter reports speech for any window of the chunk; it keeps carrying leftover samples between
/// calls, so it is not bit-identical to the native gate (<see cref="SileroChunkGate"/>), which is the default.
/// </summary>
internal sealed class VadFilterGate(IVadFilter filter, bool ownsFilter) : IChunkSpeechGate
{
    public bool ContainsSpeech(ReadOnlySpan<float> chunk) => filter.HasSpeech(chunk);

    public void Reset() => filter.Reset();

    public void Dispose()
    {
        if (ownsFilter)
            filter.Dispose();
    }
}

/// <summary>
/// Port of GenAI 0.16.0's <c>SileroVad::ContainsSpeech</c> on plain ONNX Runtime (<c>silero_vad.onnx</c> of the model
/// folder, v5 interface: <c>input [1, 576]</c>, <c>state [2, 1, 128]</c>, <c>sr [1]</c>).
/// </summary>
/// <remarks>
/// Upstream semantics that matter for dropping the same chunks: the chunk is cut into non-overlapping 512-sample
/// windows from its start, the last partial window is zero-padded and scored too, each window sees the 64 samples
/// before it as context, and the scan stops at the first window whose probability reaches the threshold, so the
/// recurrent state and the context stay where that window left them (the rest of the chunk is never scored).
/// </remarks>
internal sealed class SileroChunkGate : IChunkSpeechGate
{
    /// <summary>The only sample rate of the native gate.</summary>
    public const int SampleRateHz = 16000;

    /// <summary>Silero window at 16 kHz: 32 ms of new audio per step.</summary>
    public const int WindowSamples = 512;

    /// <summary>Samples of left context prepended to each window.</summary>
    public const int ContextSamples = 64;

    private const int StateSize = 2 * 1 * 128;

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _input = new float[ContextSamples + WindowSamples];
    private readonly float[] _state = new float[StateSize];
    private readonly float[] _nextState = new float[StateSize];
    private readonly long[] _sampleRate = new long[1];
    private readonly float[] _probability = new float[1];
    private readonly float[] _padded = new float[WindowSamples];
    private readonly OrtValue _inputValue;
    private readonly OrtValue _stateValue;
    private readonly OrtValue _sampleRateValue;
    private readonly OrtValue _probabilityValue;
    private readonly OrtValue _nextStateValue;
    private readonly string[] _inputNames = ["input", "state", "sr"];
    private readonly string[] _outputNames = ["output", "stateN"];
    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;

    public SileroChunkGate(string modelPath, float threshold, int sampleRate = SampleRateHz)
    {
        if (sampleRate != SampleRateHz)
            throw new NotSupportedException($"The Silero chunk gate supports 16 kHz only, got {sampleRate} Hz.");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Silero VAD model not found: {modelPath}", modelPath);

        Threshold = Math.Clamp(threshold, 0f, 1f);
        _sampleRate[0] = sampleRate;

        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        OrtCpuTuning.DisableThreadSpinning(options);
        try
        {
            _session = new InferenceSession(modelPath, options);
        }
        finally
        {
            options.Dispose();
        }

        foreach (var name in _inputNames)
            if (!_session.InputMetadata.ContainsKey(name))
            {
                _session.Dispose();
                throw new NotSupportedException($"{Path.GetFileName(modelPath)} has no input \"{name}\"; only the Silero VAD v5 interface (input, state, sr) is supported.");
            }

        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, ContextSamples + WindowSamples]);
        _stateValue = OrtValue.CreateTensorValueFromMemory(_state, [2, 1, 128]);
        _sampleRateValue = OrtValue.CreateTensorValueFromMemory(_sampleRate, [1]);
        _probabilityValue = OrtValue.CreateTensorValueFromMemory(_probability, [1, 1]);
        _nextStateValue = OrtValue.CreateTensorValueFromMemory(_nextState, [2, 1, 128]);
        _inputs = [_inputValue, _stateValue, _sampleRateValue];
        _outputs = [_probabilityValue, _nextStateValue];
    }

    /// <summary>Speech probability at or above which a window counts as speech.</summary>
    public float Threshold { get; set; }

    /// <summary>Probability of the most recently scored window.</summary>
    public float LastProbability => _probability[0];

    public bool ContainsSpeech(ReadOnlySpan<float> chunk)
    {
        var offset = 0;
        for (; offset + WindowSamples <= chunk.Length; offset += WindowSamples)
            if (ScoreWindow(chunk.Slice(offset, WindowSamples)) >= Threshold)
                return true;

        if (offset < chunk.Length)
        {
            Array.Clear(_padded);
            chunk[offset..].CopyTo(_padded);
            if (ScoreWindow(_padded) >= Threshold)
                return true;
        }
        return false;
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_input, 0, ContextSamples); // the context lives at the head of the input buffer
        _probability[0] = 0f;
    }

    private float ScoreWindow(ReadOnlySpan<float> window)
    {
        // [ context (last 64 samples of the previous window) | window ]; the context stays in place at the head
        window.CopyTo(_input.AsSpan(ContextSamples));
        _session.Run(_runOptions, _inputNames, _inputs, _outputNames, _outputs);
        var probability = _probability[0];

        Array.Copy(_nextState, _state, StateSize);
        window[(WindowSamples - ContextSamples)..].CopyTo(_input.AsSpan(0, ContextSamples));
        return probability;
    }

    public void Dispose()
    {
        _inputValue.Dispose();
        _stateValue.Dispose();
        _sampleRateValue.Dispose();
        _probabilityValue.Dispose();
        _nextStateValue.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
    }
}
