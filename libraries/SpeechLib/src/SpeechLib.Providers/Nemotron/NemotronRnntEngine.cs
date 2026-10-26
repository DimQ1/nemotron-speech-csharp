using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpeechLib.Nemotron;

/// <summary>
/// The model side of the streaming recognizer, behind a seam so the buffering, silence-drop and timeline logic
/// can be tested without any model: turn one chunk of raw audio into the RNN-T tokens of that chunk.
/// </summary>
internal interface INemotronChunkEngine : IDisposable
{
    /// <summary>Samples per chunk (8960 or 17920 for the shipped models).</summary>
    int ChunkSamples { get; }

    /// <summary>Encoder frames per chunk (7 or 14).</summary>
    int FramesPerChunk { get; }

    /// <summary>Raw samples that must follow a chunk before it can be encoded: 0 in faithful mode, 40 in clean mode.</summary>
    int LookaheadSamples { get; }

    /// <summary>True when the encoder takes a language id.</summary>
    bool SupportsLanguage { get; }

    /// <summary>Language id used by the next encoder call.</summary>
    int LanguageId { get; set; }

    /// <summary>Subtracted from the blank log-probability before the argmax.</summary>
    float BlankPenalty { get; set; }

    /// <summary>Where the graphs run and why; a model-free engine runs on the CPU.</summary>
    NemotronProviderInfo Provider => NemotronProviderInfo.Cpu(null);

    /// <summary>
    /// Back to the start of a stream for the active branch: zero caches, front-end state and decoder state. The engine has no
    /// clock (chunk indices come from the caller), so this is a model-state-only reset.
    /// </summary>
    void Reset();

    /// <summary>How many branches of the stream the engine can hold at once (1, or 2 for the pause-state policy).</summary>
    int BranchCount => 1;

    /// <summary>The branch whose state <see cref="DecodeChunk"/> and <see cref="Reset"/> work on (0 at the start).</summary>
    int ActiveBranch => 0;

    /// <summary>
    /// Makes <paramref name="branch"/> (0 to <see cref="BranchCount"/> - 1) the active branch; the state of the branch that was
    /// active is kept as it is. A branch that was never used starts as a reset stream.
    /// </summary>
    void SelectBranch(int branch)
    {
        if (branch != 0)
            throw new NotSupportedException("This engine holds one branch of the stream.");
    }

    /// <summary>
    /// Encodes and decodes one chunk of exactly <see cref="ChunkSamples"/> samples and appends its tokens.
    /// </summary>
    /// <param name="chunk">The chunk's raw samples.</param>
    /// <param name="lookahead">The samples that follow it (at most <see cref="LookaheadSamples"/>), or empty.</param>
    /// <param name="chunkIndex">Index of the chunk on the stream grid.</param>
    /// <param name="sink">Receives the emitted tokens.</param>
    /// <returns>The number of tokens appended.</returns>
    int DecodeChunk(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookahead, int chunkIndex, List<NemotronToken> sink);
}

/// <summary>How the three ONNX Runtime sessions of the engine are created.</summary>
/// <param name="ExecutionProvider">
/// Requested provider for the encoder: <c>cpu</c> or <c>webgpu[:hp|:lp]</c> (see <see cref="NemotronProviderPolicy"/> for when WebGPU is used);
/// every other value, and every request that cannot be carried out, is the CPU. The prediction network and the joint always run on the CPU.
/// </param>
/// <param name="CpuThreads">Intra-op threads of every CPU graph; null derives them from the core count like the GenAI recognizer did.</param>
/// <param name="SequentialExecution">ORT_SEQUENTIAL (true, default), ORT_PARALLEL (false) or the ORT default (null).</param>
/// <param name="CleanLastFrame">Give the last mel frame of a chunk its real 40 samples of look-ahead (see <see cref="NemotronStreamingMel"/>).</param>
/// <param name="Services">The plugin, crash marker, gate and session factory; null uses the real ones (tests pass fakes).</param>
/// <param name="BatchedJoint">One joint run for the remaining frames of a chunk per prediction output (see <see cref="RnntGreedyDecoder.BatchedJoint"/>).</param>
internal sealed record NemotronEngineOptions(
    string ExecutionProvider = "cpu",
    int? CpuThreads = null,
    bool? SequentialExecution = true,
    bool CleanLastFrame = false,
    NemotronProviderServices? Services = null,
    bool BatchedJoint = true);

/// <summary>
/// The Nemotron cache-aware streaming transducer on plain ONNX Runtime: mel front-end, encoder with its three
/// caches, and the greedy RNN-T search: token for token identical to ORT GenAI 0.16.0's <c>nemotron_speech</c>
/// pipeline on every clip of the golden set on Windows x64 (the platform of the oracle; the mel is bit-identical
/// there). Other platforms use their own math library for the mel, see <see cref="NemotronStreamingMel"/>: same tokens
/// on the clips tested on Linux x64, not proven beyond them.
/// </summary>
/// <remarks>
/// <para>Static shapes come from the graph metadata (mel rows per chunk, frames per chunk, cache sizes), so the 560 ms
/// and 1120 ms exports and any other chunk size need no code change. The encoder input tensor is the persistent
/// buffer <c>[9 cache rows | chunk rows]</c>: the front-end writes the chunk rows in place and the last 9 rows are
/// copied to the head after the run. Encoder caches ping-pong between two buffers that are bound as input and
/// output, so a chunk allocates nothing on the managed heap and copies none of the 6 MB of cache.</para>
/// <para><b>Branches.</b> The caches live in a <see cref="NemotronStreamState"/>. A second state is created on the first
/// <see cref="SelectBranch"/> of branch 1 (about 13 MB for the 560 ms export: its own ping-pong pair); switching binds that
/// state's caches and copies the few kilobytes of front-end and decoder state, so two branches of one stream can be decoded
/// chunk by chunk side by side and either one continued.</para>
/// </remarks>
internal sealed class NemotronRnntEngine : INemotronChunkEngine
{
    private readonly NemotronModelConfig _config;
    private readonly NemotronSessions _sessions;
    private readonly RunOptions _runOptions;
    private readonly NemotronStreamingMel _mel;
    private readonly NemotronOnnxStepModel _stepModel;
    private readonly RnntGreedyDecoder _decoder;

    private readonly int _melRows;          // rows of the encoder input: cache + chunk
    private readonly int _cacheRows;        // pre-encode cache rows
    private readonly int _numMels;
    private readonly float[] _audioSignal;
    private readonly long[] _length = new long[1];
    private readonly long[] _languageId = new long[1];
    private readonly float[] _encoderOutput;
    private readonly long[] _encodedLength = new long[1];

    // the stream states of the branches; the active one's caches are bound to the encoder, the others keep their small parts
    private readonly NemotronStreamState?[] _states = new NemotronStreamState?[2];
    private readonly long[] _channelShape;
    private readonly long[] _timeShape;
    private int _active;

    private readonly OrtValue _audioSignalValue;
    private readonly OrtValue _lengthValue;
    private readonly OrtValue? _languageValue;
    private readonly OrtValue _encoderOutputValue;
    private readonly OrtValue _encodedLengthValue;

    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;
    private const int InChannel = 2, InTime = 3, InLength = 4;       // positions of the caches in _inputs
    private const int OutChannel = 2, OutTime = 3, OutLength = 4;    // ... and in _outputs

    private Exception? _webGpuFailure;   // the run that failed on WebGPU: the next Reset moves the encoder to the CPU

    private NemotronRnntEngine(string modelDirectory, NemotronModelConfig config, NemotronEngineOptions options)
    {
        _config = config;
        _numMels = config.NumMels;
        _cacheRows = config.PreEncodeCacheSize;

        // Everything below owns native memory (sessions, pinned tensors, run options). A throw anywhere in this
        // constructor must release what was created so far: a wrong model folder picked again and again in a long-lived
        // app would otherwise leave about 13 MB of pinned arrays and the sessions behind each time.
        try
        {
            _runOptions = new RunOptions();
            _sessions = NemotronSessions.Create(modelDirectory, config, options);

            var e = config.Encoder;
            var inputs = _sessions.Encoder.InputMetadata;
            var outputs = _sessions.Encoder.OutputMetadata;
            SupportsLanguage = !string.IsNullOrEmpty(e.LanguageInput) && inputs.ContainsKey(e.LanguageInput);
            RequireNames(inputs, [e.AudioInput, e.LengthInput, e.CacheChannelInput, e.CacheTimeInput, e.CacheChannelLengthInput], "encoder input");
            RequireNames(outputs, [e.EncodedOutput, e.LengthOutput, e.CacheChannelOutput, e.CacheTimeOutput, e.CacheChannelLengthOutput], "encoder output");
            RequireFloat(inputs[e.AudioInput], e.AudioInput);
            RequireFloat(inputs[e.CacheChannelInput], e.CacheChannelInput);
            RequireFloat(inputs[e.CacheTimeInput], e.CacheTimeInput);
            RequireFloat(outputs[e.EncodedOutput], e.EncodedOutput);

            // Static shapes from the graph; a dynamic axis falls back to the config's arithmetic.
            _melRows = StaticDim(inputs[e.AudioInput], 1) ?? config.EncoderInputFrames;
            if (_melRows != config.EncoderInputFrames)
                throw new InvalidDataException(
                    $"The encoder takes {_melRows} mel rows per chunk but {NemotronModelConfig.ConfigFileName} implies {config.EncoderInputFrames} " +
                    $"(chunk_samples {config.ChunkSamples} / hop_length {config.HopLength} + pre_encode_cache_size {config.PreEncodeCacheSize}).");
            if (StaticDim(inputs[e.AudioInput], 2) is { } melBins && melBins != config.NumMels)
                throw new InvalidDataException($"The encoder takes {melBins} mel bins, {NemotronModelConfig.ConfigFileName} says {config.NumMels}.");

            FramesPerChunk = StaticDim(outputs[e.EncodedOutput], 1) ?? config.EncoderFramesPerChunk;
            if (FramesPerChunk <= 0 || config.ChunkSamples % FramesPerChunk != 0)
                throw new InvalidDataException($"The encoder yields {FramesPerChunk} frames per chunk, which does not divide chunk_samples {config.ChunkSamples}.");
            if (StaticDim(outputs[e.EncodedOutput], 2) is { } hidden && hidden != e.HiddenSize)
                throw new InvalidDataException($"The encoder output is {hidden} wide, {NemotronModelConfig.ConfigFileName} says hidden_size {e.HiddenSize}.");

            _channelShape = [1, e.NumLayers, config.LeftContext, e.HiddenSize];
            _timeShape = [1, e.NumLayers, e.HiddenSize, config.ConvContext];
            CheckCacheShape(inputs[e.CacheChannelInput], _channelShape, e.CacheChannelInput);
            CheckCacheShape(inputs[e.CacheTimeInput], _timeShape, e.CacheTimeInput);

            _mel = new NemotronStreamingMel(config);
            if (_mel.FramesFor(config.ChunkSamples) != config.MelFramesPerChunk)
                throw new InvalidDataException($"The front-end yields {_mel.FramesFor(config.ChunkSamples)} frames per chunk, expected {config.MelFramesPerChunk}.");
            LookaheadSamples = options.CleanLastFrame ? _mel.LookaheadFor(config.ChunkSamples) : 0;

            _audioSignal = new float[_melRows * _numMels];
            _audioSignalValue = OrtValue.CreateTensorValueFromMemory(_audioSignal, [1, _melRows, _numMels]);
            _length[0] = _melRows;
            _lengthValue = OrtValue.CreateTensorValueFromMemory(_length, [1]);
            if (SupportsLanguage)
                _languageValue = OrtValue.CreateTensorValueFromMemory(_languageId, [1]);

            _encoderOutput = new float[FramesPerChunk * e.HiddenSize];
            _encoderOutputValue = OrtValue.CreateTensorValueFromMemory(_encoderOutput, [1, FramesPerChunk, e.HiddenSize]);
            _encodedLengthValue = OrtValue.CreateTensorValueFromMemory(_encodedLength, [1]);

            _stepModel = new NemotronOnnxStepModel(_sessions.Decoder, _sessions.Joint, config, _encoderOutput);
            _decoder = new RnntGreedyDecoder(_stepModel, config.BlankId, config.MaxSymbolsPerStep)
            {
                BlankPenalty = config.BlankPenalty,
                BatchedJoint = options.BatchedJoint,
            };
            _states[0] = CreateState();
            var (channel, time, length) = _states[0]!.InputCaches;
            var (channelNext, timeNext, lengthNext) = _states[0]!.OutputCaches;

            var inputNames = new List<string> { e.AudioInput, e.LengthInput, e.CacheChannelInput, e.CacheTimeInput, e.CacheChannelLengthInput };
            var inputValues = new List<OrtValue> { _audioSignalValue, _lengthValue, channel, time, length };
            if (SupportsLanguage)
            {
                inputNames.Add(e.LanguageInput);
                inputValues.Add(_languageValue!);
            }
            _inputNames = inputNames.ToArray();
            _inputs = inputValues.ToArray();
            _outputNames = [e.EncodedOutput, e.LengthOutput, e.CacheChannelOutput, e.CacheTimeOutput, e.CacheChannelLengthOutput];
            _outputs = [_encoderOutputValue, _encodedLengthValue, channelNext, timeNext, lengthNext];
            if (_sessions.Provider.EncoderOnWebGpu)
                WarmUpWebGpuEncoder();
            Reset();
        }
        catch
        {
            ReleaseNativeBuffers();
            _runOptions?.Dispose();
            _sessions?.Dispose();
            throw;
        }
    }

    /// <summary>Loads the three graphs of a model folder.</summary>
    public static NemotronRnntEngine Create(string modelDirectory, NemotronModelConfig config, NemotronEngineOptions? options = null)
        => new(modelDirectory, config, options ?? new NemotronEngineOptions());

    public int ChunkSamples => _config.ChunkSamples;
    public int FramesPerChunk { get; }
    public int LookaheadSamples { get; }
    public bool SupportsLanguage { get; }

    public int LanguageId
    {
        get => (int)_languageId[0];
        set => _languageId[0] = value;
    }

    public float BlankPenalty
    {
        get => _decoder.BlankPenalty;
        set => _decoder.BlankPenalty = value;
    }

    /// <summary>Where the graphs run and why; the encoder moves to the CPU if a WebGPU run fails (see <see cref="Reset"/>).</summary>
    public NemotronProviderInfo Provider => _sessions.Provider;

    /// <summary>The run options of the encoder's runs (tests set <c>Terminate</c> to make the next run fail the way a lost GPU device would).</summary>
    internal RunOptions EncoderRunOptions => _runOptions;

    /// <summary>Prediction-network runs so far (diagnostics: GenAI would have run it once per frame and token).</summary>
    public long PredictionCalls => _decoder.PredictionCalls;

    /// <summary>Joint runs so far (diagnostics).</summary>
    public long JointCalls => _decoder.JointCalls;

    /// <summary>Encoder frames scored by the joint so far (diagnostics; more than the frames decoded when batched).</summary>
    public long JointFrames => _decoder.JointFrames;

    /// <summary>Wall-clock time in the joint and in the prediction network so far (diagnostics).</summary>
    public TimeSpan JointTime => _decoder.JointTime;

    /// <inheritdoc cref="JointTime"/>
    public TimeSpan PredictionTime => _decoder.PredictionTime;

    /// <summary>Frames whose joint output was all NaN so far; each was treated as a blank (diagnostics).</summary>
    public long NonFiniteJoints => _decoder.NonFiniteJoints;

    /// <summary>One joint run per prediction output for the rest of the chunk (true) or one per frame (false); same tokens.</summary>
    internal bool BatchedJoint
    {
        get => _decoder.BatchedJoint;
        set => _decoder.BatchedJoint = value;
    }

    /// <summary>The prediction and joint networks (tests compare batched joint rows with one-frame runs on real frames).</summary>
    internal IRnntStepModel StepModel => _stepModel;

    /// <summary>The token the prediction network is conditioned on.</summary>
    internal int LastToken => _decoder.LastToken;

    public void Reset()
    {
        if (_webGpuFailure is { } failure)
        {
            // The stream is undefined after a failed run and WebGPU ran it: carry on with the CPU instead of failing the same way again.
            _sessions.ReplaceEncoderWithCpu($"a run failed: {failure.Message}");
            _webGpuFailure = null;
        }

        _mel.Reset();
        Array.Clear(_audioSignal, 0, _cacheRows * _numMels); // the first chunk sees 9 all-zero cache rows
        var state = _states[_active]!;
        state.Clear();
        BindCaches(state);
        _decoder.Reset();
    }

    public int BranchCount => _states.Length;

    public int ActiveBranch => _active;

    public void SelectBranch(int branch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(branch);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(branch, _states.Length);
        if (branch == _active)
            return;

        // put the active branch aside: its encoder caches stay in its state object, the small parts are copied out
        var current = _states[_active]!;
        Array.Copy(_audioSignal, current.MelCacheRows, current.MelCacheRows.Length);
        current.MelPrevious = _mel.SaveState(current.MelOverlap);
        _stepModel.SaveState(current.DecoderH, current.DecoderC);
        current.LastToken = _decoder.LastToken;

        var next = _states[branch] ??= CreateState();   // a new state is all zeros: a reset stream
        Array.Copy(next.MelCacheRows, _audioSignal, next.MelCacheRows.Length);
        _mel.LoadState(next.MelOverlap, next.MelPrevious);
        _stepModel.LoadState(next.DecoderH, next.DecoderC);
        _decoder.Resume(next.LastToken);
        BindCaches(next);
        _active = branch;
    }

    public int DecodeChunk(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookahead, int chunkIndex, List<NemotronToken> sink)
    {
        if (chunk.Length != _config.ChunkSamples)
            throw new ArgumentException($"The engine encodes chunks of {_config.ChunkSamples} samples, got {chunk.Length}.", nameof(chunk));

        // rows [cache, cache + M) of the encoder input take the new mel frames; the head keeps the previous chunk's last rows
        var melFrames = _audioSignal.AsSpan(_cacheRows * _numMels);
        _mel.Process(chunk, lookahead, melFrames);

        try
        {
            _sessions.Encoder.Run(_runOptions, _inputNames, _inputs, _outputNames, _outputs);
        }
        catch (Exception e) when (_sessions.Provider.EncoderOnWebGpu)
        {
            _webGpuFailure = e;
            throw;
        }
        _sessions.EncoderRan();

        // next chunk's cache = last rows of this input; the encoder's *_next outputs become the next inputs
        Array.Copy(_audioSignal, (_melRows - _cacheRows) * _numMels, _audioSignal, 0, _cacheRows * _numMels);
        var state = _states[_active]!;
        state.SwapCaches();
        BindCaches(state);

        var timeSteps = (int)Math.Min(FramesPerChunk, _encodedLength[0]);
        return _decoder.DecodeChunk(timeSteps, chunkIndex, FramesPerChunk, sink);
    }

    public void Dispose()
    {
        ReleaseNativeBuffers();
        _runOptions.Dispose();
        _sessions.Dispose();
    }

    private NemotronStreamState CreateState() =>
        new(_channelShape, _timeShape, _cacheRows * _numMels, _mel.OverlapLength, _stepModel.StateLength, _config.BlankId);

    // The encoder reads the state's input caches and writes its output caches.
    private void BindCaches(NemotronStreamState state)
    {
        (_inputs[InChannel], _inputs[InTime], _inputs[InLength]) = state.InputCaches;
        (_outputs[OutChannel], _outputs[OutTime], _outputs[OutLength]) = state.OutputCaches;
    }

    // Disposes the step model and the pinned tensors; every member may still be unset when the constructor failed half way.
    private void ReleaseNativeBuffers()
    {
        _stepModel?.Dispose();
        _audioSignalValue?.Dispose();
        _lengthValue?.Dispose();
        _languageValue?.Dispose();
        _encoderOutputValue?.Dispose();
        _encodedLengthValue?.Dispose();
        foreach (var state in _states)
            state?.Dispose();
    }

    // ── session setup ───────────────────────────────────────────────────

    /// <summary>
    /// One throw-away run of the encoder on silence, while the model loads. The first run on WebGPU builds the pipelines of about
    /// 1750 dispatches: 0.4 to 0.6 s with a warm driver cache, 2.7 s the first time, 45 s on a cold integrated GPU. Paid here it
    /// is part of loading; paid in the first chunk it would be a visible stall at the start of the user's speech. The caller resets
    /// the engine afterwards, so the stream starts untouched. A first run that throws is the deterministic kind of WebGPU failure
    /// (driver, unsupported operator): the encoder is replaced by a CPU one right here, before any audio.
    /// </summary>
    private void WarmUpWebGpuEncoder()
    {
        try
        {
            _sessions.Encoder.Run(_runOptions, _inputNames, _inputs, _outputNames, _outputs);
            _sessions.EncoderRan();
        }
        catch (Exception e)
        {
            _sessions.ReplaceEncoderWithCpu($"the first run failed: {e.Message}");
        }
    }

    /// <summary>
    /// Intra-op thread heuristic of the GenAI recognizer (hybrid CPUs: half the logical cores; large parts keep
    /// headroom for the host threads), kept so both recognizers use the same default.
    /// </summary>
    internal static int ComputeOptimalIntraThreads(int logicalCores) => logicalCores switch
    {
        <= 4 => Math.Max(1, logicalCores - 1),
        <= 16 => logicalCores / 2,
        <= 32 => Math.Max(8, logicalCores - 8),
        _ => Math.Min(24, logicalCores - 8),
    };

    private static int? StaticDim(NodeMetadata node, int axis)
    {
        var dims = node.Dimensions;
        return axis < dims.Length && dims[axis] > 0 ? dims[axis] : null;
    }

    private static void RequireNames(IReadOnlyDictionary<string, NodeMetadata> metadata, string[] names, string what)
    {
        foreach (var name in names)
            if (!metadata.ContainsKey(name))
                throw new InvalidDataException($"The encoder graph has no {what} named \"{name}\" (found: {string.Join(", ", metadata.Keys)}).");
    }

    private static void RequireFloat(NodeMetadata node, string name)
    {
        if (node.ElementDataType != TensorElementType.Float)
            throw new NotSupportedException($"Encoder tensor \"{name}\" is {node.ElementDataType}; only float32 Nemotron graphs are supported.");
    }

    private static void CheckCacheShape(NodeMetadata node, long[] expected, string name)
    {
        var dims = node.Dimensions;
        if (dims.Length != expected.Length)
            throw new InvalidDataException($"Cache input \"{name}\" has rank {dims.Length}, expected {expected.Length}.");
        for (var i = 1; i < dims.Length; i++)
            if (dims[i] > 0 && dims[i] != expected[i])
                throw new InvalidDataException(
                    $"Cache input \"{name}\" is [{string.Join(", ", dims)}] but {NemotronModelConfig.ConfigFileName} implies [{string.Join(", ", expected)}].");
    }
}
