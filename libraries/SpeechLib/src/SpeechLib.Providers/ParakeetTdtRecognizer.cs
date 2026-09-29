using System.Runtime.InteropServices;
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
/// <para><b>Streaming.</b> The encoder uses full ("regular") attention, so NeMo's
/// cache-aware streaming is not available for this export. Instead every step
/// encodes one window <c>[left context | uncommitted audio]</c> and splits its
/// frames in two:</para>
/// <list type="bullet">
///   <item>frames that already have <c>rightContext</c> of audio after them are
///   <b>committed</b>: decoded with the persistent TDT decoder state and released
///   as final text;</item>
///   <item>the trailing <c>rightContext</c> is <b>previewed</b>: decoded from a copy
///   of the decoder state and exposed as revisable partial text
///   (<see cref="PartialText"/> / <see cref="StreamingResult.Partial"/>), then
///   discarded.</item>
/// </list>
/// <para>A step runs every <c>chunk</c> of new audio, so words show up within about
/// one chunk of being spoken and are committed one right-context later. The decoder
/// state (and the TDT duration overshoot) is carried across steps, so committed text
/// is contiguous. Trailing silence closes the utterance without waiting for the next
/// word: once the last committed token is older than <c>stopHistoryEou</c> and the
/// preview holds no speech, the held text is finalized.</para>
/// </summary>
public sealed class ParakeetTdtRecognizer : IStreamingSpeechRecognizer, IUtteranceStreamingRecognizer
{
    private static readonly Regex DecodeSpacePattern = new(@"\A\s|\s\B|(\s)\b", RegexOptions.Compiled);

    /// <summary>Audio samples per encoder frame (160 samples/mel frame × 8 subsampling).</summary>
    private const int SamplesPerFrame = 1280;

    /// <summary>Encoder output width.</summary>
    private const int EncoderDim = 1024;

    /// <summary>Flattened size of each TDT decoder LSTM state ([2, 1, 640]).</summary>
    private const int StateSize = 2 * 640;

    /// <summary>Shortest window worth sending through the encoder for a preview.</summary>
    private const int MinPreviewSamples = 6 * SamplesPerFrame; // 0.48 s

    /// <summary>
    /// Ceiling on the silence threshold (≈ −54 dBFS peak). The effective threshold is the
    /// smaller of this and 1 % of the loudest peak seen in the session, so a very quiet
    /// recording (Common Voice has clips peaking at −55 dBFS) is never gated as silence.
    /// </summary>
    private const float SilencePeakCeiling = 0.002f;
    private const float SilencePeakRatio = 0.01f;
    private float _maxPeak;

    /// <summary>
    /// Shortest encoder window from which tokens are committed. The first seconds of a
    /// session have no left context; the encoder normalises features over the window, so
    /// committing from a very short window mis-hears the first word (the preview still
    /// shows it immediately, revisable).
    /// </summary>
    private const int MinCommitSamples = 48 * SamplesPerFrame; // 3.84 s

    /// <summary>Default step: text shows within a third of a second of being spoken.</summary>
    public const double DefaultChunkSeconds = 0.32;

    /// <summary>Default right context for words inside an utterance.</summary>
    public const double DefaultRightContextSeconds = 1.0;

    /// <summary>Default audio prepended to every encoder window.</summary>
    public const double DefaultLeftContextSeconds = 5.0;

    /// <summary>Default right context before token-free audio is left behind.</summary>
    public const double DefaultSilenceContextSeconds = 2.0;

    /// <summary>Default right context for the first word of an utterance.</summary>
    public const double DefaultOnsetContextSeconds = 2.0;

    private readonly InferenceSession _preprocessor;   // nemo128.onnx
    private readonly InferenceSession _encoder;        // encoder-model.onnx
    private readonly InferenceSession _decoderJoint;   // decoder_joint-model.onnx

    private readonly Dictionary<int, string> _vocab = new();
    private readonly HashSet<int> _wordStartIds = new();
    private readonly int _vocabSize;
    private readonly int _blankIdx;
    private readonly int _maxTokensPerStep;

    // ── Streaming buffer ────────────────────────────────────────────────
    private readonly List<float> _audio = new();
    private long _trimmedSamples;   // samples dropped from the head of _audio (absolute offset of _audio[0])
    private int _committedSamples;  // committed boundary, relative to _audio[0] (frame-aligned)
    private int _examinedSamples;   // relative end of the frames decoded with full right context in the last step
    private int _steppedSamples;    // _audio.Count when the last step ran
    private readonly int _chunkSamples;
    private readonly int _leftSamples;
    private readonly int _rightSamples;
    private readonly int _onsetSamples;
    private readonly int _silenceSamples;
    private readonly int _stopEouSamples;
    private readonly bool _previewEnabled;
    private double _lastStepSeconds;    // compute time of the last encoder step (adaptive step rate)

    // ── Decoder state ───────────────────────────────────────────────────
    private DecoderState _state;                          // committed decoder state
    private DecoderState? _carriedState;                  // state before the last pause reset (onset fallback)
    private readonly List<int> _previewIds = new();       // revisable tokens for the uncommitted tail
    private List<int> _eouSplits = new();                 // committed-token indices that start a new utterance
    private int _lastTokenCount;
    private bool _disposed;

    // ── Delta output (IStreamingSpeechRecognizer) ──────────────────────
    // Committed tokens waiting for the word they belong to to finish (streaming output
    // must not commit half a word at a step boundary).
    private readonly List<int> _heldIds = new();
    private bool _emittedAnyText;

    // ── Utterance output (IUtteranceStreamingRecognizer) ───────────────
    private readonly StringBuilder _partial = new();      // committed, not yet finalized text since the last EoU
    private readonly StringBuilder _pendingFinal = new(); // utterances finalized during the current step

    /// <inheritdoc />
    public int SampleRate => 16000;

    /// <inheritdoc />
    public int ChunkSamples => 1600; // 100 ms at 16 kHz; callers may feed any batch size

    /// <inheritdoc />
    public double StopHistoryEouSeconds { get; }

    /// <inheritdoc />
    public int LastTokenCount => _lastTokenCount;

    /// <summary>Step / commit granularity in seconds (rounded to whole encoder frames).</summary>
    public double ChunkSeconds => _chunkSamples / 16000.0;

    /// <summary>Audio kept after a frame before it is committed, in seconds (rounded to whole frames).</summary>
    public double RightContextSeconds => _rightSamples / 16000.0;

    /// <summary>Audio prepended to each encoder window, in seconds (rounded to whole frames).</summary>
    public double LeftContextSeconds => _leftSamples / 16000.0;

    /// <summary>Future audio a token-free (silent) stretch needs before it is left behind, in seconds.</summary>
    public double SilenceContextSeconds => _silenceSamples / 16000.0;

    /// <summary>Future audio the first word of an utterance needs before it is committed, in seconds.</summary>
    public double OnsetContextSeconds => _onsetSamples / 16000.0;

    /// <summary>Audio a fresh session must accumulate before anything is committed (preview is not delayed), in seconds.</summary>
    public double CommitWarmupSeconds => MinCommitSamples / 16000.0;

    /// <summary>True when the uncommitted tail is decoded into revisable partial text on every step.</summary>
    public bool PreviewEnabled => _previewEnabled;

    /// <summary>
    /// Loads a quantization folder (fp32 / int8 / int4) of the exported model.
    /// The folder must contain encoder-model.onnx, decoder_joint-model.onnx,
    /// nemo128.onnx, vocab.txt and config.json (standard onnx-asr names, no
    /// quantization suffix — the folder itself selects the precision).
    /// </summary>
    /// <param name="modelDir">Path to the quantization folder (e.g. .../int8).</param>
    /// <param name="chunkSeconds">New audio that triggers a step; also the commit granularity.</param>
    /// <param name="leftContextSeconds">Audio context prepended to each encoder window.</param>
    /// <param name="rightContextSeconds">Future audio a frame must have before it is committed.
    /// Larger = better accuracy at step boundaries, higher commit latency.</param>
    /// <param name="stopHistoryEouSeconds">Silence (seconds of consecutive blank frames)
    /// that closes an utterance for blank-based endpointing.</param>
    /// <param name="executionProvider">Requested provider: "cpu", "cuda" or "dml".
    /// Falls back to CPU when the requested provider is unavailable.</param>
    /// <param name="previewPartials">Decode the uncommitted tail into revisable partial
    /// text on every step (costs one extra decoder pass, the encoder pass is shared).</param>
    /// <param name="silenceContextSeconds">Future audio a stretch that produced no token
    /// needs before the committed boundary moves past it. Sentence onsets after a pause
    /// need more right context than words inside a sentence (see <see cref="Step"/>).</param>
    /// <param name="onsetContextSeconds">Future audio the first word of an utterance needs
    /// before it is committed; later words use <paramref name="rightContextSeconds"/>.</param>
    public ParakeetTdtRecognizer(
        string modelDir,
        double chunkSeconds = DefaultChunkSeconds,
        double leftContextSeconds = DefaultLeftContextSeconds,
        double rightContextSeconds = DefaultRightContextSeconds,
        double stopHistoryEouSeconds = 0.8,
        string executionProvider = "cpu",
        bool previewPartials = true,
        double silenceContextSeconds = DefaultSilenceContextSeconds,
        double onsetContextSeconds = DefaultOnsetContextSeconds)
    {
        var dir = Path.GetFullPath(modelDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Model directory not found: {dir}");

        var options = CreateSessionOptions(executionProvider);
        _preprocessor = new InferenceSession(Path.Combine(dir, "nemo128.onnx"), options);
        _encoder = new InferenceSession(Path.Combine(dir, "encoder-model.onnx"), options);
        _decoderJoint = new InferenceSession(Path.Combine(dir, "decoder_joint-model.onnx"), options);

        LoadVocab(Path.Combine(dir, "vocab.txt"));
        _vocabSize = _vocab.Count;
        _blankIdx = _vocab.First(kv => kv.Value == "<blk>").Key;

        _maxTokensPerStep = LoadMaxTokensPerStep(Path.Combine(dir, "config.json"));

        // Window boundaries are cut on whole encoder frames so committed frames line up
        // exactly from one step to the next (5 s = 62.5 frames would drift half a frame).
        _chunkSamples = ToFrameSamples(chunkSeconds, minFrames: 1);
        _leftSamples = ToFrameSamples(leftContextSeconds, minFrames: 0);
        _rightSamples = ToFrameSamples(rightContextSeconds, minFrames: 0);
        _silenceSamples = Math.Max(_rightSamples, ToFrameSamples(silenceContextSeconds, minFrames: 0));
        _onsetSamples = Math.Max(_rightSamples, ToFrameSamples(onsetContextSeconds, minFrames: 0));
        _stopEouSamples = (int)(16000 * stopHistoryEouSeconds);
        _previewEnabled = previewPartials;
        StopHistoryEouSeconds = stopHistoryEouSeconds;
        _state = new DecoderState(_blankIdx);
    }

    private static int ToFrameSamples(double seconds, int minFrames)
    {
        var frames = (int)Math.Round(seconds * 16000 / SamplesPerFrame);
        return Math.Max(minFrames, frames) * SamplesPerFrame;
    }

    // ------------------------------------------------------------------ //
    // IStreamingSpeechRecognizer (delta output)                           //
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public string? ProcessAudio(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.AddRange(chunk);

        var ids = Step(flush: false);
        // Trailing silence proves the held word is complete: release it now instead of
        // waiting for the next word (or Flush) to confirm the boundary.
        var pause = HasTrailingSilence();
        if (pause)
            StartNewUtteranceState();
        return DetokenizeCompleteWords(ids, flush: pause);
    }

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return DetokenizeCompleteWords(Step(flush: true), flush: true);
    }

    /// <inheritdoc />
    public string? PartialText
    {
        get
        {
            if (_heldIds.Count == 0 && _previewIds.Count == 0)
                return "";

            var ids = new List<int>(_heldIds.Count + _previewIds.Count);
            ids.AddRange(_heldIds);
            ids.AddRange(_previewIds);
            return Detokenize(ids);
        }
    }

    // ------------------------------------------------------------------ //
    // IUtteranceStreamingRecognizer (partial / final output)              //
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public StreamingResult ProcessUtterance(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.AddRange(chunk);

        AppendUtterances(Step(flush: false));
        if (HasTrailingSilence())
        {
            FinalizeCurrentPartial();
            StartNewUtteranceState();
        }

        return TakeResult();
    }

    /// <inheritdoc />
    public StreamingResult FlushUtterance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppendUtterances(Step(flush: true));
        FinalizeCurrentPartial();
        return TakeResult();
    }

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.Clear();
        _trimmedSamples = 0;
        _committedSamples = 0;
        _examinedSamples = 0;
        _steppedSamples = 0;
        _maxPeak = 0f;
        _lastStepSeconds = 0;
        _state = new DecoderState(_blankIdx);
        _carriedState = null;
        _previewIds.Clear();
        _eouSplits = new List<int>();
        _lastTokenCount = 0;
        _heldIds.Clear();
        _emittedAnyText = false;
        _partial.Clear();
        _pendingFinal.Clear();
    }

    /// <summary>Transcribe a complete 16 kHz mono float waveform (independent of the streaming state).</summary>
    public string Transcribe(float[] waveform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var encodings = Encode(waveform);
        var state = new DecoderState(_blankIdx);
        var decoded = DecodeFrames(encodings, 0, encodings.Length, 0, state);
        return Detokenize(decoded.Tokens);
    }

    // ------------------------------------------------------------------ //
    // Streaming step                                                       //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Runs one streaming step when a chunk of new audio has arrived (always on flush).
    /// Encodes <c>[left | open region]</c> once and decodes the open region from a copy of
    /// the committed decoder state in two phases:
    /// <list type="number">
    ///   <item>frames with at least <c>rightContext</c> of audio after them — tokens
    ///   emitted here are committed;</item>
    ///   <item>the remaining tail — tokens emitted here are the revisable preview.</item>
    /// </list>
    /// The committed boundary moves only to the end of the last committed token, never
    /// through silence: greedy TDT predicts "blank, skip 4 frames" through a pause, and a
    /// sentence onset needs more right context than a word mid-sentence before the joint
    /// prefers it over blank. Frames that produced no token are decoded again on the
    /// next step with more context, until they are <c>silenceContext</c> old — then they
    /// are left behind to bound the encoder window. Returns the newly committed token
    /// ids and leaves their utterance splits in <see cref="_eouSplits"/>.
    /// </summary>
    private List<int> Step(bool flush)
    {
        var committed = new List<int>();
        _eouSplits = new List<int>();
        _lastTokenCount = 0;

        // Adaptive step rate: never step more often than the previous step took to compute,
        // so a slow machine degrades to longer steps instead of falling behind real time.
        int minNewAudio = Math.Max(_chunkSamples, (int)(_lastStepSeconds * 1.25 * 16000));
        if (!flush && _audio.Count - _steppedSamples < minNewAudio)
            return committed;
        TrackPeak(_steppedSamples, _audio.Count - _steppedSamples);
        _steppedSamples = _audio.Count;

        int open = _audio.Count - _committedSamples;
        if (open <= 0)
        {
            _previewIds.Clear();
            return committed;
        }

        // The first word of an utterance (fresh decoder state) needs more right context
        // than the words after it before the joint reliably prefers it over blank.
        int right = _state.LastToken == _blankIdx ? _onsetSamples : _rightSamples;

        int windowStart = Math.Max(0, _committedSamples - _leftSamples);
        int windowLength = _audio.Count - windowStart;

        // Phase-1 extent: everything but the trailing right context, on whole frames.
        // Nothing is committed from a window too short to have proper context.
        int examineSamples = flush
            ? open
            : windowLength < MinCommitSamples
                ? 0
                : Math.Max(0, open - right) / SamplesPerFrame * SamplesPerFrame;
        bool preview = _previewEnabled && !flush;
        bool examineGrew = _committedSamples + examineSamples > _examinedSamples;
        if (!flush && !examineGrew && !preview)
            return committed;
        if (!flush && !examineGrew && windowLength < MinPreviewSamples)
            return committed;

        // Silence gate: a near-silent open region with nothing pending carries no
        // information, so account for it as blank without running the encoder.
        if (!flush && _previewIds.Count == 0 && IsNearSilent(_committedSamples, open))
        {
            SkipAsSilence(examineSamples);
            return committed;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var window = CollectionsMarshal.AsSpan(_audio).Slice(windowStart, windowLength).ToArray();
        var encodings = Encode(window);
        long windowStartAbs = _trimmedSamples + windowStart;
        int totalFrames = encodings.Length;
        int leftFrames = (_committedSamples - windowStart) / SamplesPerFrame;
        int examineEnd = flush
            ? totalFrames
            : Math.Min(totalFrames, leftFrames + examineSamples / SamplesPerFrame);

        var scratch = _state.Clone();
        int nextFrame = leftFrames;
        if (examineEnd > leftFrames)
        {
            var decoded = DecodeFrames(encodings, leftFrames, examineEnd, windowStartAbs, scratch);

            // Onset fallback: a pause may or may not have been a sentence boundary. When
            // the fresh state hears nothing in a region that does contain speech, the
            // state carried from before the pause gets the same frames — it often catches
            // an onset the fresh state scores just below blank.
            if (decoded.Tokens.Count == 0 && _carriedState is not null && _state.LastToken == _blankIdx
                && !IsNearSilent(_committedSamples, examineSamples))
            {
                var carried = _carriedState.Clone();
                var retry = DecodeFrames(encodings, leftFrames, examineEnd, windowStartAbs, carried);
                if (retry.Tokens.Count > 0)
                {
                    decoded = retry;
                    scratch = carried;
                }
            }

            committed = decoded.Tokens;
            _eouSplits = decoded.EouSplits;
            nextFrame = decoded.NextFrame;

            if (committed.Count > 0)
            {
                // Commit through the last token's own duration; what follows is re-decoded
                // next step from this state (a repeated frame after a token yields blank).
                _state = scratch.Clone();
                _carriedState = null;
                // The boundary may run past the examined frontier when the last token's
                // duration does: those frames belong to that token, re-decoding them from
                // the post-token state can emit it again ("starve started").
                int boundary = Math.Min(totalFrames, decoded.LastTokenEnd);
                _committedSamples = windowStart + boundary * SamplesPerFrame;
                examineEnd = Math.Max(examineEnd, boundary);
            }
            else if (examineEnd - leftFrames > _silenceSamples / SamplesPerFrame)
            {
                // A long token-free stretch: keep only its most recent silenceContext open.
                _committedSamples = windowStart + (examineEnd - _silenceSamples / SamplesPerFrame) * SamplesPerFrame;
            }

            _examinedSamples = windowStart + examineEnd * SamplesPerFrame;
        }

        if (flush)
        {
            _committedSamples = _audio.Count;
            _examinedSamples = _audio.Count;
        }

        _previewIds.Clear();
        if (preview && nextFrame < totalFrames)
            _previewIds.AddRange(DecodeFrames(encodings, nextFrame, totalFrames, windowStartAbs, scratch).Tokens);

        _lastTokenCount = committed.Count;
        _lastStepSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        if (TraceEnabled)
        {
            Console.Error.WriteLine(
                $"[parakeet] step flush={flush} audio={_trimmedSamples + _audio.Count} committed={_trimmedSamples + _committedSamples} " +
                $"examined={_trimmedSamples + _examinedSamples} window={windowStart}+{windowLength} frames={totalFrames} left={leftFrames} " +
                $"examineEnd={examineEnd} right={right / SamplesPerFrame}f tokens={committed.Count} preview={_previewIds.Count} " +
                $"lastEmit={_state.LastEmitSample} ms={_lastStepSeconds * 1000:F0}");
        }

        TrimConsumedAudio();
        return committed;
    }

    private void TrackPeak(int start, int count)
    {
        if (count <= 0)
            return;
        var peak = Peak(CollectionsMarshal.AsSpan(_audio).Slice(start, count));
        if (peak > _maxPeak)
            _maxPeak = peak;
    }

    private bool IsNearSilent(int start, int count)
    {
        if (count <= 0)
            return true;
        var threshold = Math.Min(SilencePeakCeiling, _maxPeak * SilencePeakRatio);
        return Peak(CollectionsMarshal.AsSpan(_audio).Slice(start, count)) < threshold;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>
    /// Account for a skipped (near-silent) step exactly as if its frames had been decoded
    /// and found blank: the examined frontier and the silence boundary advance, so
    /// endpointing and the encoder-window bound behave the same as on a decoded step.
    /// </summary>
    private void SkipAsSilence(int examineSamples)
    {
        int examinedEnd = _committedSamples + examineSamples;
        if (examinedEnd > _examinedSamples)
            _examinedSamples = examinedEnd;

        if (examineSamples > _silenceSamples)
            _committedSamples = examinedEnd - _silenceSamples;

        if (TraceEnabled)
            Console.Error.WriteLine($"[parakeet] step skipped as silence: audio={_trimmedSamples + _audio.Count} committed={_trimmedSamples + _committedSamples} examined={_trimmedSamples + _examinedSamples}");

        TrimConsumedAudio();
    }

    /// <summary>Per-step diagnostics on stderr when SPEECHLIB_PARAKEET_TRACE=1.</summary>
    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("SPEECHLIB_PARAKEET_TRACE") == "1";

    /// <summary>
    /// Forget the prediction-network context once an utterance has ended in silence, so
    /// the next sentence is decoded the way NeMo decodes a segmented utterance: from a
    /// fresh state. Greedy TDT carried past a sentence-final token through a pause is
    /// biased towards blank at the next onset and can skip a whole sentence (offline
    /// decoding of a multi-sentence recording shows the same loss); a fresh state removes
    /// that bias while the encoder still sees the full audio left context. The last-emit
    /// position is kept so blank-gap endpointing keeps working.
    /// </summary>
    private void StartNewUtteranceState()
    {
        if (_state.LastToken == _blankIdx)
            return; // already fresh
        _carriedState = _state;
        _state = new DecoderState(_blankIdx) { LastEmitSample = _state.LastEmitSample };
    }

    /// <summary>
    /// True when the audio after the last committed token is silence longer than the
    /// end-of-utterance threshold. Evidence is either decoded blank frames with full right
    /// context (the preview tail is blank-biased and would end the utterance mid-sentence)
    /// or, for a fast decision in a quiet room, near-silent audio right up to now.
    /// A preview that did emit tokens vetoes both.
    /// </summary>
    private bool HasTrailingSilence()
    {
        if (_state.LastEmitSample < 0)
            return false;
        if (_previewIds.Count > 0)
            return false;

        long lastEmit = _state.LastEmitSample;
        if (_trimmedSamples + _examinedSamples - lastEmit > _stopEouSamples)
            return true;

        // Audio since one frame after the last token is below the silence floor.
        long quietFrom = lastEmit + SamplesPerFrame - _trimmedSamples;
        long quietLength = _audio.Count - quietFrom;
        return quietFrom >= 0
               && quietLength > _stopEouSamples
               && IsNearSilent((int)quietFrom, (int)quietLength);
    }

    /// <summary>
    /// Drop fully-consumed audio from the head of the buffer once the kept tail
    /// exceeds twice the left context. Prevents unbounded growth of _audio (and the
    /// window copies in every step) during long sessions. Absolute positions are kept
    /// in <see cref="_trimmedSamples"/> so blank-gap endpointing survives the trim.
    /// </summary>
    private void TrimConsumedAudio()
    {
        int removable = _committedSamples - _leftSamples;
        if (removable <= _leftSamples)
            return;

        _audio.RemoveRange(0, removable);
        _committedSamples -= removable;
        _examinedSamples -= removable;
        _steppedSamples -= removable;
        _trimmedSamples += removable;
    }

    // ------------------------------------------------------------------ //
    // Utterance segmentation                                               //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Splits a step's committed token ids at blank-detected end-of-utterance boundaries,
    /// appending continuation text to <see cref="_partial"/> and committing
    /// completed utterances to <see cref="_pendingFinal"/>.
    /// </summary>
    private void AppendUtterances(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return;

        // A blank run may span the step boundary: if the first token of this
        // step already starts a new utterance, close the current partial first.
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
        return new StreamingResult(PartialWithPreview(), final) { Stable = _partial.ToString() };
    }

    /// <summary>The committed-but-unfinalized utterance text followed by the revisable preview.</summary>
    private string PartialWithPreview()
    {
        if (_previewIds.Count == 0)
            return _partial.ToString();

        var preview = Detokenize(_previewIds);
        if (preview.Length == 0)
            return _partial.ToString();
        if (_partial.Length == 0)
            return preview;

        // A preview that continues the last committed word joins it without a space.
        return _wordStartIds.Contains(_previewIds[0])
            ? _partial + " " + preview
            : _partial + preview;
    }

    // ------------------------------------------------------------------ //
    // Delta output                                                         //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Emits only the committed ids that form complete words and holds the trailing word
    /// back until a later step (trailing silence, or <see cref="Flush"/>) confirms it.
    /// </summary>
    /// <remarks>
    /// Step boundaries fall wherever the audio happens to be, so committing the raw
    /// detokenization split words in half: a trace showed "disappro" followed by
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
            // Hold from the last word start onwards (that word may still be growing).
            // No word start at all means the held ids continue a word whose start was
            // already released, so there is nothing to protect.
            int lastWordStart = -1;
            for (int i = _heldIds.Count - 1; i >= 0; i--)
            {
                if (_wordStartIds.Contains(_heldIds[i]))
                {
                    lastWordStart = i;
                    break;
                }
            }

            releaseEnd = lastWordStart < 0 ? _heldIds.Count : lastWordStart;

            // An end-of-utterance split proves everything before it is complete.
            if (_eouSplits.Count > 0)
                releaseEnd = Math.Max(releaseEnd, heldBefore + _eouSplits[^1]);
        }

        if (releaseEnd <= 0)
            return null;

        var complete = _heldIds.GetRange(0, releaseEnd);
        _heldIds.RemoveRange(0, releaseEnd);
        return DetokenizeChunk(complete);
    }

    /// <summary>
    /// Detokenizes committed ids and decides whether they need a leading space
    /// when concatenated with the previous delta. A space is added only when the
    /// delta begins a NEW word (its first token carries the SentencePiece ▁
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

    /// <summary>True when a delta needs a leading space: it starts a new word and
    /// some text was already emitted (no leading space on the very first word).</summary>
    private static bool ShouldPrefixSpace(bool startsWord, bool alreadyEmitted)
        => startsWord && alreadyEmitted;

    // ------------------------------------------------------------------ //
    // Inference pipeline                                                  //
    // ------------------------------------------------------------------ //

    /// <summary>Persistent TDT decoder state plus the endpointing / duration bookkeeping tied to it.</summary>
    private sealed class DecoderState
    {
        public float[] State1 = new float[StateSize];
        public float[] State2 = new float[StateSize];
        public int LastToken;

        /// <summary>Absolute sample of the frame that emitted the last token; −1 before any token.</summary>
        public long LastEmitSample = -1;

        public DecoderState(int blankIdx) => LastToken = blankIdx;

        public DecoderState Clone() => new(LastToken)
        {
            State1 = (float[])State1.Clone(),
            State2 = (float[])State2.Clone(),
            LastEmitSample = LastEmitSample,
        };
    }

    /// <param name="Tokens">Emitted token ids.</param>
    /// <param name="EouSplits">Indices into <paramref name="Tokens"/> that start a new utterance.</param>
    /// <param name="LastTokenEnd">First frame after the last emitted token's duration (the frame
    /// the decoder moved to right after emitting it), or −1 when no token was emitted.</param>
    /// <param name="NextFrame">Frame the decoder would visit next (may exceed the decoded range).</param>
    private readonly record struct DecodeOutput(
        List<int> Tokens, List<int> EouSplits, int LastTokenEnd, int NextFrame);

    private float[][] Encode(float[] waveform)
    {
        // 1) log-mel features: waveforms [1,N] -> features [1,128,T], features_lens [1]
        var (features, featuresLens) = RunPreprocessor(waveform);

        // 2) encoder: audio_signal [1,128,T] -> encodings [T_enc, 1024]
        var (encodings, _) = RunEncoder(features, featuresLens);
        return encodings;
    }

    /// <summary>
    /// TDT greedy decode over encoder frames [startFrame, endFrame), advancing
    /// <paramref name="state"/> in place. <paramref name="windowStartSample"/> is the
    /// absolute position of frame 0 so blank gaps are measured across steps and trims.
    /// </summary>
    private DecodeOutput DecodeFrames(
        float[][] encodings, int startFrame, int endFrame, long windowStartSample, DecoderState state)
    {
        var tokens = new List<int>();
        var splits = new List<int>();
        int lastTokenEnd = -1;

        int t = startFrame;
        int emitted = 0;

        while (t < endFrame)
        {
            var (logits, nextState1, nextState2) = RunDecoderJoint(encodings, t, state);

            // logits = [vocab (vocabSize)] + [duration (decoderDim - vocabSize)]
            int token = ArgMax(logits, 0, _vocabSize);
            int duration = ArgMax(logits, _vocabSize, logits.Length - _vocabSize);

            if (TraceEnabled)
            {
                int best = -1; float bestLogit = float.NegativeInfinity;
                for (int i = 0; i < _vocabSize; i++)
                    if (i != _blankIdx && logits[i] > bestLogit) { bestLogit = logits[i]; best = i; }
                Console.Error.WriteLine($"[parakeet]   t={t} token={token} dur={duration} blankLogit={logits[_blankIdx]:F2} best={best}('{(best >= 0 && _vocab.TryGetValue(best, out var bt) ? bt : "?")}')={bestLogit:F2} last={state.LastToken}");
            }

            if (token != _blankIdx)
            {
                // Blank-based endpointing: the silence between two emitted tokens
                // is the run of blank frames between them. A gap longer than
                // _stopEouSamples marks the start of a new utterance.
                long absSample = windowStartSample + (long)t * SamplesPerFrame;
                if (state.LastEmitSample >= 0 && absSample - state.LastEmitSample > _stopEouSamples)
                    splits.Add(tokens.Count);
                state.LastEmitSample = absSample;

                tokens.Add(token);
                state.State1 = nextState1;
                state.State2 = nextState2;
                state.LastToken = token;
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

            if (token != _blankIdx)
                lastTokenEnd = t;
        }

        return new DecodeOutput(tokens, splits, lastTokenEnd, t);
    }

    private static SessionOptions CreateSessionOptions(string executionProvider)
    {
        // The encoder re-encodes the whole left+uncommitted window on every step and is
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

        int tEnc = (int)encLens[0];
        int totalFrames = (int)(outputs.Length / EncoderDim);   // batch(1) * T_enc
        if (TraceEnabled)
            Console.Error.WriteLine($"[parakeet] encoder mel={t} featuresLens={featuresLens} encLen={tEnc} frames={totalFrames}");

        // Transpose [1, 1024, T_enc] -> [T_enc, 1024] (row per frame)
        var encodings = new float[totalFrames][];
        for (int frame = 0; frame < totalFrames; frame++)
        {
            var row = new float[EncoderDim];
            for (int dim = 0; dim < EncoderDim; dim++)
                row[dim] = outputs[0, dim, frame];
            encodings[frame] = row;
        }

        return (encodings, tEnc);
    }

    private (float[] logits, float[] state1, float[] state2) RunDecoderJoint(
        float[][] encodings, int t, DecoderState state)
    {
        // encoder_outputs [1, 1024, 1] — single frame
        var encoderOutputs = new DenseTensor<float>(encodings[t], new[] { 1, EncoderDim, 1 });

        var targets = new DenseTensor<int>(new[] { 1, 1 });
        targets[0, 0] = state.LastToken;
        var targetLength = new DenseTensor<int>(new[] { 1 });
        targetLength[0] = 1;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("encoder_outputs", encoderOutputs),
            NamedOnnxValue.CreateFromTensor("targets", targets),
            NamedOnnxValue.CreateFromTensor("target_length", targetLength),
            NamedOnnxValue.CreateFromTensor("input_states_1", new DenseTensor<float>(state.State1, new[] { 2, 1, 640 })),
            NamedOnnxValue.CreateFromTensor("input_states_2", new DenseTensor<float>(state.State2, new[] { 2, 1, 640 })),
        };

        using var results = _decoderJoint.Run(inputs);
        var logits = results[0].AsTensor<float>().ToArray();   // [1, 1, vocab + duration]
        var next1 = results[2].AsTensor<float>().ToArray();    // [2, 1, 640]
        var next2 = results[3].AsTensor<float>().ToArray();    // [2, 1, 640]
        return (logits, next1, next2);
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
            if (raw.StartsWith('▁'))
                _wordStartIds.Add(id);

            // Special tokens (<unk>, <pad>, <|nospeech|>, ...) carry no text; <blk> keeps its
            // name because the blank id is located by it.
            _vocab[id] = raw == "<blk>" || !(raw.StartsWith('<') && raw.EndsWith('>'))
                ? raw.Replace("▁", " ") // ▁ -> space
                : "";
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
