using System.Text;
using SpeechLib.Audio;

namespace SpeechLib.Nemotron;

/// <summary>Options of <see cref="NemotronRnntRecognizer"/> beyond the model folder.</summary>
public sealed record NemotronRecognizerOptions
{
    /// <summary>
    /// Intra-op threads of the CPU graphs (the encoder, or the few CPU nodes next to a WebGPU encoder, the prediction network and the
    /// joint); null derives them from the core count.
    /// </summary>
    public int? CpuThreads { get; init; }

    /// <summary>ORT_SEQUENTIAL (true, default), ORT_PARALLEL (false) or the ORT default (null).</summary>
    public bool? SequentialExecution { get; init; } = true;

    /// <summary>Overrides <c>search.blank_penalty</c> of the model folder.</summary>
    public float? BlankPenalty { get; init; }

    /// <summary>
    /// Give the last mel frame of every chunk its real next 40 samples instead of GenAI's 56 zeros (what NeMo
    /// computes). Changes the token ids of about 5 percent of the clips (word error rate unchanged); off by default
    /// for token-exact parity with GenAI. <b>Latency</b>: a chunk is held back until 40 samples (2.5 ms of audio) of the
    /// next chunk are buffered, so a caller that feeds chunk-sized pieces gets every chunk one feed later: the extra
    /// delay is up to one feed interval (200 ms with the apps' 3200-sample batches, 560 ms or 1120 ms with
    /// chunk-sized feeds), not 2.5 ms. <see cref="NemotronRnntRecognizer.Flush"/> encodes what is still waiting.
    /// </summary>
    public bool CleanLastFrame { get; init; }

    /// <summary>
    /// Run the joint network once for all remaining encoder frames of a chunk per prediction output (true, default)
    /// instead of once per frame and symbol like GenAI (false). The rows are bit-identical to one-frame runs, so the
    /// tokens and their scores are the same (verified against the GenAI oracle on Windows x64); false is the reference
    /// path for comparisons.
    /// </summary>
    public bool BatchedJoint { get; init; } = true;

    /// <summary>
    /// Replaces the native silence gate (Silero on the model folder's <c>silero_vad.onnx</c>, scored per chunk
    /// exactly like GenAI) with an external voice-activity filter. The recognizer does not dispose it.
    /// </summary>
    public IVadFilter? VadFilter { get; init; }

    /// <summary>Path of a Silero VAD model; defaults to the file named by the model folder's <c>vad</c> block.</summary>
    public string? VadModelPath { get; init; }

    /// <summary>
    /// What the model state does when speech resumes after a pause; <see cref="NemotronPauseState.Keep"/> (GenAI parity) by default.
    /// The policy needs the silence gate: the one of the silence drop rule when it is on, else a gate of its own from the same
    /// <see cref="VadFilter"/> / <see cref="VadModelPath"/> / model folder (without one the policy stays off, with a warning).
    /// </summary>
    public NemotronPauseStateOptions PauseState { get; init; } = NemotronPauseStateOptions.Parity;

    /// <summary>
    /// Chunks of zeros <see cref="NemotronRnntRecognizer.Flush"/> encodes after the zero-padded remainder (0, the default, is GenAI's
    /// single padded chunk). The model writes the last tokens of a word up to a chunk after the audio that holds it, so a stream that
    /// ends right after the last word (Stop pressed while speaking, a file cut at the last word) loses them with one padded chunk;
    /// one more chunk gives them back (measured -1.7 WER points in English, -0.8 in Russian on clips cut 100 ms after the speech;
    /// a second chunk changes nothing). They are encoded only when the stream has audio the model has not seen silence after
    /// (a non-empty remainder, or a last chunk that was not dropped), cost one encoder pass each, and stay on the stream grid like
    /// the padding.
    /// </summary>
    public int FlushClosingChunks { get; init; }
}

/// <summary>
/// Nemotron cache-aware streaming recognizer on plain ONNX Runtime: a drop-in replacement for the GenAI
/// <c>ModelSession</c> that produces the same tokens and text (verified token for token on Windows x64; other
/// platforms see <see cref="NemotronStreamingMel"/> for what was measured), with a real
/// <see cref="ResetStreamingState"/>, batches of any size, and the frame position of every token.
/// </summary>
/// <remarks>
/// <para><b>Chunking.</b> Audio is buffered into chunks of <see cref="ChunkSamples"/> (560 ms or 1120 ms) at multiples
/// of the chunk size of the stream fed since the last reset; one encoder call per chunk, any number of chunks per
/// <see cref="ProcessAudio(float[])"/> call, so results never depend on how the caller batched the audio.
/// <see cref="Flush"/> zero-pads a non-empty remainder to a chunk in the raw-audio domain and does nothing when the
/// remainder is empty (no extra tail chunk).</para>
/// <para><b>Return values</b> follow <c>ModelSession</c>: null when no chunk was encoded by the call (not enough audio,
/// or the chunk was dropped as silence), otherwise the decoded text delta, possibly empty. Text is final the moment it
/// is returned. Language tags (<c>&lt;en-US&gt;</c>) are part of the text like in GenAI.</para>
/// <para><b>Silence drop</b> (<see cref="TrySetVad"/>): the native GenAI rule, see <see cref="SilenceDropRule"/>.
/// A dropped chunk touches no model state but keeps its place on the timeline.</para>
/// <para><b>Pauses</b> (<see cref="NemotronRecognizerOptions.PauseState"/>): GenAI keeps the model state through every pause; the
/// policy can give the speech after a pause a fresh state, or decode it both ways and keep the better branch. With a branch
/// decision pending, the text of the chunks under decision is reported by the call that ends the decision.</para>
/// <para><b>Timeline</b>: see <see cref="NemotronToken"/>. <see cref="LastTokens"/> and <see cref="LastChunks"/> describe
/// what the most recent call produced, for a layer that builds word timings and utterances on top.</para>
/// <para>Not thread-safe, like <c>ModelSession</c>.</para>
/// </remarks>
public sealed class NemotronRnntRecognizer : IStreamingSpeechRecognizer, ILanguageConfigurable, IRuntimeConfigurable
{
    // GenAI's own defaults for a stream without a vad block would drop from the first silent chunk; this keeps
    // the values of the shipped folders for an injected filter on a folder that has no block.
    private static readonly NemotronVadConfig FallbackVad = new() { Threshold = 0.5f, SilenceDurationMs = 3360, PrefixPaddingMs = 560 };

    private readonly INemotronChunkEngine _engine;
    private readonly NemotronModelConfig _config;
    private readonly Func<IChunkSpeechGate> _createGate;
    private readonly int _lookahead;
    private bool _disposed;

    private IChunkSpeechGate? _gate;
    private SilenceDropRule? _dropRule;

    private float[] _buffer;
    private int _buffered;
    private readonly float[] _paddedChunk;

    private readonly List<NemotronToken> _tokens = new();
    private readonly List<NemotronChunkInfo> _chunks = new();
    private readonly StringBuilder _text = new();
    private bool _encodedAny;
    private Exception? _fault;

    // closing chunks at Flush: the model decoded audio since the last flush that no silence followed
    private readonly int _flushClosingChunks;
    private bool _openTail;

    // pause-state policy
    private readonly NemotronPauseState _pausePolicy;
    private readonly int _pauseChunks;        // silent chunks that make a pause
    private readonly int _decisionChunks;     // chunks both branches decode before the decision
    private readonly int _prerollChunks;      // silent chunks fed to a fresh state in front of the speech
    private readonly int _freshLead;          // letter tokens the fresh branch must write more than the kept one
    private readonly PrerollRing? _ring;
    private bool[]? _lexical;
    private SilenceDropRule? _pauseCounter;   // the policy's own speech gate while the silence drop is off
    private IChunkSpeechGate? _pauseGate;
    private bool _pauseGateFailed;
    private bool _heardSpeech;                // the active model state has decoded speech since the last reset or decision
    private BranchWindow? _window;            // the pending decision
    private readonly BranchWindow _windowStore = new();
    private readonly List<NemotronToken> _scratch = new();

    /// <summary>Loads a Nemotron model folder (see <see cref="NemotronModelConfig.IsNemotronModel"/>).</summary>
    /// <param name="modelPath">Model folder with <c>genai_config.json</c>, the three graphs and <c>vocab.txt</c>.</param>
    /// <param name="executionProvider">
    /// <c>cpu</c> (default) or <c>webgpu[:hp|:lp]</c>. WebGPU runs the encoder only, on a discrete graphics card; every other value and every
    /// request that cannot be carried out (no plugin, no discrete card, <c>lp</c>, a crash marker, a failure) is the CPU, see
    /// <see cref="ExecutionProvider"/> for what was chosen and why. The prediction network and the joint always run on the CPU.
    /// </param>
    /// <param name="langId">Language code or id (see <see cref="LanguageMapper"/>); null keeps lang_id 0 (en-US).</param>
    /// <param name="useVad">Start with the silence drop rule enabled.</param>
    /// <param name="options">Threads, blank penalty and the optional clean front-end / external VAD.</param>
    public NemotronRnntRecognizer(string modelPath, string executionProvider = "cpu", string? langId = null, bool useVad = false,
        NemotronRecognizerOptions? options = null)
        : this(LoadParts(modelPath, executionProvider, options ??= new NemotronRecognizerOptions()), langId, useVad, options)
    {
    }

    private NemotronRnntRecognizer(LoadedParts parts, string? langId, bool useVad, NemotronRecognizerOptions options)
        : this(parts.Engine, parts.Config, parts.Vocabulary, () => CreateGate(parts.Directory, parts.Config, options), useVad, langId, options)
    {
        if (options.BlankPenalty is { } penalty)
            BlankPenalty = penalty;
    }

    /// <summary>
    /// Test seam: a recognizer over any chunk engine. The recognizer owns the engine from the moment it is passed in:
    /// when the constructor throws it has disposed it (the public constructor has no other handle to it).
    /// </summary>
    /// <param name="options">Only the stream policies are read here (<see cref="NemotronRecognizerOptions.PauseState"/>,
    /// <see cref="NemotronRecognizerOptions.FlushClosingChunks"/>); the engine is built already.</param>
    internal NemotronRnntRecognizer(INemotronChunkEngine engine, NemotronModelConfig config, NemotronVocabulary vocabulary,
        Func<IChunkSpeechGate> createGate, bool useVad = false, string? langId = null, NemotronRecognizerOptions? options = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        try
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
            _createGate = createGate ?? throw new ArgumentNullException(nameof(createGate));

            if (engine.ChunkSamples <= 0 || engine.FramesPerChunk <= 0 || engine.ChunkSamples % engine.FramesPerChunk != 0)
                throw new ArgumentException("The engine's chunk and frame counts are inconsistent.", nameof(engine));

            _lookahead = engine.LookaheadSamples;
            _buffer = new float[engine.ChunkSamples + _lookahead];
            _paddedChunk = new float[engine.ChunkSamples];

            options ??= new NemotronRecognizerOptions();
            ArgumentOutOfRangeException.ThrowIfNegative(options.FlushClosingChunks, nameof(options));
            _flushClosingChunks = options.FlushClosingChunks;
            var pause = options.PauseState ?? NemotronPauseStateOptions.Parity;
            _pausePolicy = pause.Policy;
            if (_pausePolicy != NemotronPauseState.Keep)
            {
                if (engine.BranchCount < 2)
                    throw new ArgumentException($"The pause-state policy {_pausePolicy} needs an engine that holds two branches of the stream.", nameof(engine));
                if (!(pause.PauseSeconds > 0 && pause.DecisionChunks > 0 && pause.PrerollSeconds >= 0))
                    throw new ArgumentOutOfRangeException(nameof(options), "The pause duration and the decision chunks must be positive, the preroll not negative.");
                var chunkSeconds = (double)engine.ChunkSamples / config.SampleRate;
                _pauseChunks = NemotronPauseStateOptions.ToChunks(pause.PauseSeconds, chunkSeconds);
                _decisionChunks = pause.DecisionChunks;
                if (pause.FreshLead < 1)
                    throw new ArgumentOutOfRangeException(nameof(options), pause.FreshLead, "FreshLead must be at least 1.");
                _freshLead = pause.FreshLead;
                _prerollChunks = pause.PrerollSeconds > 0 ? NemotronPauseStateOptions.ToChunks(pause.PrerollSeconds, chunkSeconds) : 0;
                _ring = new PrerollRing(_prerollChunks, engine.ChunkSamples, _lookahead);
            }

            if (!IsSingleLanguage && langId is not null)
                TrySetLanguage(langId);
            if (useVad)
                TrySetVad(true);
        }
        catch
        {
            DisableVad();
            engine.Dispose();
            throw;
        }
    }

    // ── identity ────────────────────────────────────────────────────────

    /// <summary>Sample rate the recognizer expects (16 kHz).</summary>
    public int SampleRate => _config.SampleRate;

    /// <summary>Samples per chunk: 8960 (560 ms) or 17920 (1120 ms) for the shipped models.</summary>
    public int ChunkSamples => _engine.ChunkSamples;

    /// <summary>Encoder frames per chunk (7 or 14).</summary>
    public int FramesPerChunk => _engine.FramesPerChunk;

    /// <summary>Samples per encoder frame, the unit of <see cref="NemotronToken.GlobalFrame"/> (1280 = 80 ms).</summary>
    public int SamplesPerFrame => _engine.ChunkSamples / _engine.FramesPerChunk;

    /// <summary>True when the encoder has no language input.</summary>
    public bool IsSingleLanguage => !_engine.SupportsLanguage;

    /// <summary>The model folder's configuration.</summary>
    public NemotronModelConfig Config => _config;

    /// <summary>Vocabulary and detokenizer; its <c>StartsWord</c> and <c>PieceOf</c> feed <c>TimedWordBuilder</c>.</summary>
    public NemotronVocabulary Vocabulary { get; }

    /// <summary>
    /// Where the graphs run and why: the requested provider, the one the encoder runs on and the reason when they differ. It changes
    /// when a failed WebGPU run makes the engine move to the CPU at the next <see cref="ResetStreamingState"/>.
    /// </summary>
    public NemotronProviderInfo ExecutionProvider => _engine.Provider;

    /// <summary>The chunk engine (tests read its diagnostics counters).</summary>
    internal INemotronChunkEngine Engine => _engine;

    /// <summary>
    /// Forgets that an earlier process died while it was starting on WebGPU, so that the next recognizer tries WebGPU again (what
    /// <c>SPEECHLIB_WEBGPU_RETRY=1</c> does for one start). Meant for a Settings button or the moment the user picks the graphics card
    /// again; the marker is described in <see cref="NemotronProviderInfo.Reason"/> while it keeps WebGPU off.
    /// </summary>
    public static void ClearWebGpuCrashMarker() => WebGpuCrashMarker.Default.ClearStale();

    // ── configuration ───────────────────────────────────────────────────

    /// <summary>Language id the next encoder call uses (0 = en-US, 101 = auto).</summary>
    public int LanguageId => _engine.LanguageId;

    /// <summary>Subtracted from the blank log-probability before the argmax (default from the model folder, usually 0).</summary>
    public float BlankPenalty
    {
        get => _engine.BlankPenalty;
        set => _engine.BlankPenalty = value;
    }

    /// <summary>True while the silence drop rule is active.</summary>
    public bool VadEnabled => _dropRule is not null;

    /// <summary><c>"true"</c> or <c>"false"</c>, like GenAI's <c>use_vad</c> option.</summary>
    public string VadStatus => VadEnabled ? "true" : "false";

    /// <summary>Consecutive silent chunks after which silent chunks are dropped, or 0 when the rule is off.</summary>
    public int SilenceDropThreshold => _dropRule?.DropThreshold ?? 0;

    /// <summary>The pause-state policy in effect (<see cref="NemotronRecognizerOptions.PauseState"/>).</summary>
    public NemotronPauseState PauseState => _pausePolicy;

    /// <summary>Chunks of zeros <see cref="Flush"/> adds after the padded remainder (<see cref="NemotronRecognizerOptions.FlushClosingChunks"/>).</summary>
    public int FlushClosingChunks => _flushClosingChunks;

    /// <summary>Pauses after which the policy decoded a fresh branch, since the reset.</summary>
    public int PauseForks { get; private set; }

    /// <summary>Pauses after which the fresh branch continued the stream, since the reset.</summary>
    public int FreshBranchWins { get; private set; }

    /// <summary>Raised with every decision of the pause-state policy (diagnostics and evaluation; the texts are decoded only for a subscriber).</summary>
    public event Action<NemotronPauseDecision>? PauseDecided;

    /// <inheritdoc />
    public bool TrySetLanguage(string language)
    {
        if (IsSingleLanguage)
            return false;
        if (LanguageMapper.Resolve(language) is not { } resolved || !int.TryParse(resolved, out var id))
            return false;
        _engine.LanguageId = id;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Enabling creates the gate and clears the silent-chunk count (a gate that already exists is left alone, as in
    /// GenAI); disabling discards both. Returns false, leaving the rule off, when no VAD model can be loaded.
    /// </remarks>
    public bool TrySetVad(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                DisableVad();
                return true;
            }

            if (_dropRule is null)
            {
                var gate = _createGate();
                try
                {
                    var vad = _config.Vad ?? FallbackVad;
                    var (silenceChunks, prefixChunks) = SilenceDropRule.ChunkCounts(vad, _engine.ChunkSamples, _config.SampleRate);
                    _dropRule = new SilenceDropRule(gate, silenceChunks, prefixChunks);
                    _gate = gate;
                }
                catch
                {
                    gate.Dispose();
                    throw;
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  Warning: VAD not set ({e.Message})");
            return false;
        }
    }

    /// <summary>
    /// Accepted for compatibility with <see cref="IRuntimeConfigurable"/>. Beam count and repetition penalty are
    /// ignored by the transducer search (as they were by GenAI); returns true.
    /// </summary>
    public bool TrySetSearchOptions(int numBeams, double repetitionPenalty) => true;

    // ── timeline and the last call ──────────────────────────────────────

    /// <summary>Chunks consumed since the reset, dropped ones included: the position of the stream on the chunk grid.</summary>
    public int ChunksConsumed { get; private set; }

    /// <summary>Chunks the silence rule dropped since the reset.</summary>
    public int ChunksDropped { get; private set; }

    /// <summary>Samples of the stream covered by the chunks consumed so far (<c>ChunksConsumed * ChunkSamples</c>).</summary>
    public long StreamPositionSamples => (long)ChunksConsumed * ChunkSamples;

    /// <summary>Samples fed but not yet part of a consumed chunk.</summary>
    public int BufferedSamples => _buffered;

    /// <summary>
    /// NaN and infinite input samples replaced by 0 since the recognizer was created. One NaN would otherwise stay in the
    /// mel and encoder caches for about nine chunks (5 s) and make the search emit nothing useful (the model has no
    /// notion of it); silence is the safe reading of a sample that is not a number. A non-zero count points at a
    /// faulty audio source or resampler upstream. Finite samples, even outside [-1, 1], pass unchanged.
    /// </summary>
    public long NonFiniteSamples { get; private set; }

    /// <summary>Non-blank tokens emitted by the most recent <see cref="ProcessAudio(float[])"/> or <see cref="Flush"/> call.</summary>
    public int LastTokenCount { get; private set; }

    /// <summary>
    /// The tokens of the most recent call in emission order, with their chunk, frame and global frame. The list is
    /// reused: it is valid until the next call.
    /// </summary>
    public IReadOnlyList<NemotronToken> LastTokens => _tokens;

    /// <summary>
    /// The chunks of the most recent call (encoded and dropped); valid until the next call. While a pause-branch decision is pending
    /// its chunks are not listed; the call that decides lists them all, in stream order, with the winning branch's tokens.
    /// </summary>
    public IReadOnlyList<NemotronChunkInfo> LastChunks => _chunks;

    // ── streaming ───────────────────────────────────────────────────────

    /// <inheritdoc cref="ProcessAudio(ReadOnlySpan{float})"/>
    public string? ProcessAudio(float[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return ProcessAudio(chunk.AsSpan());
    }

    /// <summary>
    /// Feeds audio of any length. Every chunk completed by it is encoded (or dropped as silence) in order.
    /// </summary>
    /// <returns>The text decoded by the call, or null when no chunk went through the model.</returns>
    /// <exception cref="InvalidOperationException">The recognizer is faulted, see <see cref="IsFaulted"/>.</exception>
    public string? ProcessAudio(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();
        ThrowIfFaulted();
        BeginCall();

        EnsureCapacity((long)_buffered + samples.Length);
        var added = _buffer.AsSpan(_buffered, samples.Length);
        samples.CopyTo(added);
        ReplaceNonFinite(added);
        _buffered += samples.Length;

        var chunk = _engine.ChunkSamples;
        var needed = chunk + _lookahead;
        var position = 0;
        try
        {
            while (_buffered - position >= needed)
            {
                ConsumeChunk(_buffer.AsSpan(position, chunk), _buffer.AsSpan(position + chunk, _lookahead), applySilenceRule: true);
                position += chunk;
            }
        }
        catch (Exception e)
        {
            Fault(e, position);
            throw;
        }
        Discard(position);

        return EndCall();
    }

    /// <summary>
    /// Ends the stream: encodes the remainder, zero-padded to a full chunk in the raw-audio domain (the silence rule
    /// does not apply), then <see cref="FlushClosingChunks"/> chunks of zeros, and ends a pending pause-branch decision.
    /// Returns null when nothing was left. The padded and closing chunks keep their place on the timeline.
    /// </summary>
    /// <exception cref="InvalidOperationException">The recognizer is faulted, see <see cref="IsFaulted"/>.</exception>
    public string? Flush()
    {
        ThrowIfDisposed();
        ThrowIfFaulted();
        BeginCall();

        var chunk = _engine.ChunkSamples;
        var position = 0;
        try
        {
            // Only in clean mode can complete chunks still wait here, for look-ahead that never came.
            while (_buffered - position >= chunk)
            {
                var available = Math.Min(_lookahead, _buffered - position - chunk);
                ConsumeChunk(_buffer.AsSpan(position, chunk), _buffer.AsSpan(position + chunk, available), applySilenceRule: true);
                position += chunk;
            }

            var remainder = _buffered - position;
            var closing = remainder > 0 || _openTail ? _flushClosingChunks : 0;
            if (remainder > 0)
            {
                _buffer.AsSpan(position, remainder).CopyTo(_paddedChunk);
                _paddedChunk.AsSpan(remainder).Clear();
                ConsumeChunk(_paddedChunk, default, applySilenceRule: false);
                position += remainder;
            }
            for (var k = 0; k < closing; k++)
            {
                // the last tokens of a word come up to a chunk after its audio: give the model that chunk of silence
                _paddedChunk.AsSpan().Clear();
                ConsumeChunk(_paddedChunk, default, applySilenceRule: false);
            }
            if (_window is not null)
                DecideBranch();
        }
        catch (Exception e)
        {
            Fault(e, position);
            throw;
        }
        _buffered = 0;
        _openTail = false;
        TrimBuffer();

        return EndCall();
    }

    /// <summary>
    /// Starts a new stream: caches, decoder state, front-end state, buffered audio, silence count, counters and
    /// last-call results are all cleared. Language, blank penalty and the silence rule's on/off state stay.
    /// </summary>
    public void ResetStreamingState()
    {
        ThrowIfDisposed();
        _engine.Reset();
        _dropRule?.Reset();
        _pauseCounter?.Reset();
        _window = null;
        _ring?.Clear();
        _heardSpeech = false;
        _openTail = false;
        PauseForks = 0;
        FreshBranchWins = 0;
        _buffered = 0;
        ChunksConsumed = 0;
        ChunksDropped = 0;
        LastTokenCount = 0;
        _tokens.Clear();
        _chunks.Clear();
        _text.Clear();
        _encodedAny = false;
        TrimBuffer();
        _fault = null;   // last: a reset that itself fails leaves the recognizer faulted
    }

    /// <summary>
    /// Gives the stream a fresh model state (encoder caches, front-end overlap, decoder) and keeps everything else: the stream
    /// clock (<see cref="ChunksConsumed"/>, the chunk indices and so the word times of what follows), buffered audio, the silence
    /// count and the counters. What the pause-state policy does to one branch; a caller that knows a new, unrelated stream starts
    /// on the same clock (a new speaker, a new file appended) can do the same.
    /// </summary>
    /// <returns>
    /// Like a <see cref="ProcessAudio(ReadOnlySpan{float})"/> call without audio: the text of a pending pause-branch decision that the
    /// reset settles first (<see cref="LastTokens"/> and <see cref="LastChunks"/> describe it), else null.
    /// </returns>
    /// <exception cref="InvalidOperationException">The recognizer is faulted, see <see cref="IsFaulted"/>.</exception>
    public string? ResetModelState()
    {
        ThrowIfDisposed();
        ThrowIfFaulted();
        BeginCall();
        if (_window is not null)
        {
            DecideBranch();
            _encodedAny = true;
        }
        _engine.Reset();
        _heardSpeech = false;
        return EndCall();
    }

    /// <summary>
    /// True after <see cref="ProcessAudio(ReadOnlySpan{float})"/> or <see cref="Flush"/> threw while chunks were being
    /// encoded (an ONNX Runtime error, out of memory, a lost device). The stream is then undefined: the front-end, the
    /// encoder caches, the decoder and the silence rule do not all stand at the same chunk, and the text of the chunks
    /// that did succeed went out with the exception. Every later <see cref="ProcessAudio(ReadOnlySpan{float})"/> and
    /// <see cref="Flush"/> throws <see cref="InvalidOperationException"/> (inner: the original error) until
    /// <see cref="ResetStreamingState"/> starts a new stream. <see cref="ChunksConsumed"/>, <see cref="BufferedSamples"/>
    /// and <see cref="LastTokens"/> / <see cref="LastChunks"/> stay consistent with each other (they describe the chunks
    /// completed before the failure) so a caller can see how far the call got.
    /// </summary>
    public bool IsFaulted => _fault is not null;

    private void ThrowIfFaulted()
    {
        if (_fault is { } fault)
            throw new InvalidOperationException(
                $"The stream is undefined after an earlier failure ({fault.GetType().Name}: {fault.Message}); call {nameof(ResetStreamingState)}() to start a new stream.",
                fault);
    }

    // `completedSamples` of the buffer belong to chunks that went through: take them out so the counters match the buffer.
    private void Fault(Exception error, int completedSamples)
    {
        _fault = error;
        Discard(completedSamples);
        LastTokenCount = _tokens.Count;
    }

    private void ConsumeChunk(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookahead, bool applySilenceRule)
    {
        // ChunksConsumed moves only when the chunk is done, so a throw leaves it at the number of chunks that went through.
        var index = ChunksConsumed;
        var silentBefore = 0;
        bool? speech = null;   // unknown without a gate, and for the padded flush chunks
        if (applySilenceRule)
        {
            if (_dropRule is { } rule)
            {
                silentBefore = rule.ConsecutiveSilentChunks;
                var drop = rule.ShouldDrop(chunk);
                speech = rule.ConsecutiveSilentChunks == 0;
                if (drop)
                {
                    DropChunk(chunk, lookahead, index);
                    return;
                }
            }
            else if (PauseCounter() is { } counter)
            {
                silentBefore = counter.ConsecutiveSilentChunks;
                counter.ShouldDrop(chunk);   // scores the chunk; its threshold is never reached
                speech = counter.ConsecutiveSilentChunks == 0;
            }
        }

        if (_window is null && speech == true && _heardSpeech && silentBefore >= _pauseChunks && _pausePolicy != NemotronPauseState.Keep)
            OpenBranchWindow(index, silentBefore);

        if (_window is { } window)
            DecodeBothBranches(window, chunk, lookahead, index);
        else
            DecodeActiveBranch(chunk, lookahead, index);

        _heardSpeech |= speech == true;
        _ring?.Add(chunk, lookahead, index);
        if (applySilenceRule)
            _openTail = true;
        _encodedAny = true;
        ChunksConsumed = index + 1;

        if (_window is { } open && applySilenceRule && open.Chunks.Count >= _decisionChunks)
            DecideBranch();
    }

    private void DropChunk(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookahead, int index)
    {
        if (_window is not null)
            DecideBranch();   // a long pause began inside the window: decide on what both branches heard
        ChunksDropped++;
        _chunks.Add(new NemotronChunkInfo(index, Dropped: true, _tokens.Count, 0));
        _ring?.Add(chunk, lookahead, index);
        _openTail = false;
        ChunksConsumed = index + 1;
    }

    private void DecodeActiveBranch(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookahead, int index)
    {
        var first = _tokens.Count;
        int count;
        try
        {
            count = _engine.DecodeChunk(chunk, lookahead, index, _tokens);
        }
        catch
        {
            _tokens.RemoveRange(first, _tokens.Count - first);   // the half-decoded chunk leaves nothing behind
            throw;
        }
        for (var i = first; i < first + count; i++)
            Vocabulary.AppendDecoded(_text, _tokens[i].Id);
        _chunks.Add(new NemotronChunkInfo(index, Dropped: false, first, count));
    }

    // ── pause-state policy ──────────────────────────────────────────────

    // The speech gate of the policy while the silence drop rule (which scores every chunk anyway) is off: a drop rule that never
    // drops. Created on first use; a missing VAD model turns the policy off for the recognizer's lifetime, with one warning.
    private SilenceDropRule? PauseCounter()
    {
        if (_pausePolicy == NemotronPauseState.Keep || _pauseGateFailed)
            return null;
        if (_pauseCounter is null)
        {
            IChunkSpeechGate? gate = null;
            try
            {
                gate = _createGate();
                _pauseCounter = new SilenceDropRule(gate, int.MaxValue, int.MaxValue);
                _pauseGate = gate;
            }
            catch (Exception e)
            {
                gate?.Dispose();
                _pauseGateFailed = true;
                Console.WriteLine($"  Warning: the pause-state policy {_pausePolicy} needs a speech gate and stays off ({e.Message})");
                return null;
            }
        }
        return _pauseCounter;
    }

    // Speech resumes after a pause: the active state becomes the "keep" branch, the other branch is reset and hears the end of the
    // pause (the preroll) so that it starts the way a stream starts, in room tone. Its preroll tokens are not text: those chunks
    // went out with the keep branch already.
    private void OpenBranchWindow(int index, int silentBefore)
    {
        var window = _windowStore;
        window.Clear(index, silentBefore, keepBranch: _engine.ActiveBranch);
        _engine.SelectBranch(window.FreshBranch);
        _engine.Reset();
        var ring = _ring!;
        var preroll = Math.Min(_prerollChunks, Math.Min(silentBefore, ring.Count));
        for (var k = ring.Count - preroll; k < ring.Count; k++)
        {
            _scratch.Clear();
            var (samples, lookahead, chunkIndex) = ring[k];
            _engine.DecodeChunk(samples, lookahead.Span, chunkIndex, _scratch);
        }
        _scratch.Clear();
        _window = window;
        PauseForks++;
    }

    // One chunk in both branches; the active one first, so a chunk costs one switch of the branch.
    private void DecodeBothBranches(BranchWindow window, ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookahead, int index)
    {
        var first = _engine.ActiveBranch;
        _engine.DecodeChunk(chunk, lookahead, index, window.TokensOf(first));
        var second = first == window.KeepBranch ? window.FreshBranch : window.KeepBranch;
        _engine.SelectBranch(second);
        _engine.DecodeChunk(chunk, lookahead, index, window.TokensOf(second));
        window.Chunks.Add(index);
    }

    // Ends the window: the branch with more letter tokens continues (a tie keeps the state), the other one is dropped, and the
    // window's chunks are reported with the winner's tokens. A fresh winner gets the closing mark and tag of the previous
    // sentence from the kept state, which writes them when the next speech begins; consumers read the language from the tag and
    // join the texts with it (UtteranceText.Join).
    private void DecideBranch()
    {
        var window = _window!;
        _window = null;
        var lexical = LexicalTokens();
        var keepLetters = window.Keep.Count(t => lexical[t.Id]);
        var freshLetters = window.Fresh.Count(t => lexical[t.Id]);
        var freshWins = _pausePolicy == NemotronPauseState.Fresh || freshLetters >= keepLetters + _freshLead;
        _engine.SelectBranch(freshWins ? window.FreshBranch : window.KeepBranch);
        if (freshWins)
            FreshBranchWins++;

        var output = freshWins ? MergeClosers(window, lexical) : window.Keep;
        var t = 0;
        foreach (var held in window.Chunks)
        {
            var first = _tokens.Count;
            for (; t < output.Count && output[t].Chunk <= held; t++)
            {
                _tokens.Add(output[t]);
                Vocabulary.AppendDecoded(_text, output[t].Id);
            }
            _chunks.Add(new NemotronChunkInfo(held, Dropped: false, first, _tokens.Count - first));
        }

        if (PauseDecided is { } decided)
        {
            int[] PerChunk(List<NemotronToken> tokens) => window.Chunks.Select(c => tokens.Count(k => k.Chunk == c && lexical[k.Id])).ToArray();
            decided(new NemotronPauseDecision(window.FirstChunk, window.Chunks.Count, window.SilentChunks, keepLetters, freshLetters, freshWins,
                Vocabulary.Detokenize(window.Keep.Select(k => k.Id).ToArray()), Vocabulary.Detokenize(window.Fresh.Select(k => k.Id).ToArray()),
                PerChunk(window.Keep), PerChunk(window.Fresh)));
        }
    }

    // The keep branch's tokens in front of its first letter token, up to its last mark or tag (a bare word marker after it
    // belongs to the next word), then the fresh branch's tokens. A closer emitted later than the fresh branch's first token is
    // moved onto that token's frame: it belongs to the sentence before, which ends earlier.
    private List<NemotronToken> MergeClosers(BranchWindow window, bool[] lexical)
    {
        var merged = window.Merged;
        merged.Clear();
        var keep = window.Keep;
        var end = 0;
        for (var k = 0; k < keep.Count && !lexical[keep[k].Id]; k++)
        {
            var id = keep[k].Id;
            if (Vocabulary.IsTag(id) || Vocabulary.PieceOf(id).Trim().Length > 0)
                end = k + 1;
        }

        var limit = window.Fresh.Count > 0 ? window.Fresh[0].GlobalFrame : long.MaxValue;
        var framesPerChunk = _engine.FramesPerChunk;
        for (var k = 0; k < end; k++)
        {
            var token = keep[k];
            if (token.GlobalFrame > limit)
                token = token with { Chunk = (int)(limit / framesPerChunk), Frame = (int)(limit % framesPerChunk), GlobalFrame = limit };
            merged.Add(token);
        }
        merged.AddRange(window.Fresh);

        // tokens on one frame are numbered in emission order
        for (var k = 1; k < merged.Count; k++)
        {
            var symbol = merged[k].GlobalFrame == merged[k - 1].GlobalFrame ? merged[k - 1].Symbol + 1 : 0;
            if (merged[k].Symbol != symbol)
                merged[k] = merged[k] with { Symbol = symbol };
        }
        if (merged.Count > 0 && merged[0].Symbol != 0)
            merged[0] = merged[0] with { Symbol = 0 };
        return merged;
    }

    private bool[] LexicalTokens()
    {
        if (_lexical is null)
        {
            var lexical = new bool[Vocabulary.Count];
            for (var id = 0; id < lexical.Length; id++)
                lexical[id] = Vocabulary.PieceOf(id).Any(char.IsLetterOrDigit);
            _lexical = lexical;
        }
        return _lexical;
    }

    /// <summary>The chunks both branches decode while a decision is pending, and their tokens.</summary>
    private sealed class BranchWindow
    {
        public int FirstChunk { get; private set; }
        public int SilentChunks { get; private set; }
        public int KeepBranch { get; private set; }
        public int FreshBranch => 1 - KeepBranch;
        public List<int> Chunks { get; } = new();
        public List<NemotronToken> Keep { get; } = new();
        public List<NemotronToken> Fresh { get; } = new();
        public List<NemotronToken> Merged { get; } = new();

        public List<NemotronToken> TokensOf(int branch) => branch == KeepBranch ? Keep : Fresh;

        public void Clear(int firstChunk, int silentChunks, int keepBranch)
        {
            FirstChunk = firstChunk;
            SilentChunks = silentChunks;
            KeepBranch = keepBranch;
            Chunks.Clear();
            Keep.Clear();
            Fresh.Clear();
            Merged.Clear();
        }
    }

    /// <summary>The last chunks of the stream (samples and look-ahead), the preroll of a fresh branch.</summary>
    private sealed class PrerollRing(int capacity, int chunkSamples, int lookaheadSamples)
    {
        private readonly float[][] _samples = Enumerable.Range(0, capacity).Select(_ => new float[chunkSamples]).ToArray();
        private readonly float[][] _lookahead = Enumerable.Range(0, capacity).Select(_ => new float[lookaheadSamples]).ToArray();
        private readonly int[] _lookaheadLength = new int[capacity];
        private readonly int[] _index = new int[capacity];
        private int _next;

        public int Count { get; private set; }

        /// <summary>The k-th oldest chunk held.</summary>
        public (float[] Samples, ReadOnlyMemory<float> Lookahead, int Index) this[int k]
        {
            get
            {
                var slot = (_next - Count + k + capacity) % capacity;
                return (_samples[slot], _lookahead[slot].AsMemory(0, _lookaheadLength[slot]), _index[slot]);
            }
        }

        public void Add(ReadOnlySpan<float> samples, ReadOnlySpan<float> lookahead, int index)
        {
            if (capacity == 0)
                return;
            samples.CopyTo(_samples[_next]);
            var look = Math.Min(lookahead.Length, lookaheadSamples);
            lookahead[..look].CopyTo(_lookahead[_next]);
            _lookaheadLength[_next] = look;
            _index[_next] = index;
            _next = (_next + 1) % capacity;
            Count = Math.Min(Count + 1, capacity);
        }

        public void Clear()
        {
            Count = 0;
            _next = 0;
        }
    }

    private void BeginCall()
    {
        _tokens.Clear();
        _chunks.Clear();
        _text.Clear();
        _encodedAny = false;
    }

    private string? EndCall()
    {
        LastTokenCount = _tokens.Count;
        return _encodedAny ? _text.ToString() : null;
    }

    // ── buffer ──────────────────────────────────────────────────────────

    private void ReplaceNonFinite(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i]))
            {
                samples[i] = 0f;
                NonFiniteSamples++;
            }
        }
    }

    private void EnsureCapacity(long required)
    {
        if (_buffer.Length >= required)
            return;
        var size = (long)_buffer.Length;
        while (size < required)
            size *= 2;
        if (required > Array.MaxLength)
            throw new OutOfMemoryException($"{required} buffered samples do not fit into one array; feed the audio in smaller calls.");
        Array.Resize(ref _buffer, (int)Math.Min(size, Array.MaxLength));
    }

    /// <summary>Capacity of the internal input buffer in samples (tests check it is cut back after a large batch).</summary>
    internal int BufferCapacity => _buffer.Length;

    // Drops the first `count` buffered samples (consumed chunks), keeping the rest at the head.
    private void Discard(int count)
    {
        if (count > 0)
        {
            Array.Copy(_buffer, count, _buffer, 0, _buffered - count);
            _buffered -= count;
        }
        TrimBuffer();
    }

    // A caller that fed minutes of audio in one call must not leave a buffer that big behind.
    private void TrimBuffer()
    {
        var baseline = _engine.ChunkSamples + _lookahead;
        if (_buffer.Length > 16 * baseline && _buffered + baseline < _buffer.Length / 4)
            Array.Resize(ref _buffer, Math.Max(baseline, 2 * (_buffered + baseline)));
    }

    // ── lifetime ────────────────────────────────────────────────────────

    private void DisableVad()
    {
        _dropRule = null;
        _gate?.Dispose();
        _gate = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisableVad();
        _pauseGate?.Dispose();
        _pauseGate = null;
        _pauseCounter = null;
        _engine.Dispose();
    }

    // ── model loading ───────────────────────────────────────────────────

    private sealed record LoadedParts(string Directory, NemotronModelConfig Config, NemotronVocabulary Vocabulary, INemotronChunkEngine Engine);

    private static LoadedParts LoadParts(string modelPath, string executionProvider, NemotronRecognizerOptions options)
    {
        var directory = ResolvePath(modelPath);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Model path not found: {directory}");

        var config = NemotronModelConfig.Load(directory);
        var vocabulary = NemotronVocabulary.Load(Path.Combine(directory, "vocab.txt"), config.BlankId, config.VocabSize);
        var engine = NemotronRnntEngine.Create(directory, config,
            new NemotronEngineOptions(executionProvider, options.CpuThreads, options.SequentialExecution, options.CleanLastFrame,
                BatchedJoint: options.BatchedJoint));
        return new LoadedParts(directory, config, vocabulary, engine);
    }

    internal static IChunkSpeechGate CreateGate(string directory, NemotronModelConfig config, NemotronRecognizerOptions options)
    {
        if (options.VadFilter is { } filter)
            return new VadFilterGate(filter, ownsFilter: false);

        // Fail before any file is touched: the native gate is Silero v5 at 16 kHz.
        if (config.SampleRate != SileroChunkGate.SampleRateHz)
            throw new NotSupportedException(
                $"The native silence gate scores 16 kHz audio only, but model.sample_rate is {config.SampleRate} Hz. " +
                "Leave the silence drop off or pass a VadFilter.");

        var path = options.VadModelPath
            ?? (config.Vad is { HasModel: true } vad ? Path.Combine(directory, vad.FileName) : null)
            ?? throw new FileNotFoundException("The model folder's config has no vad block with a filename and no VAD model path was given.");
        return new SileroChunkGate(path, config.Vad?.Threshold ?? FallbackVad.Threshold, config.SampleRate);
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", path));
}
