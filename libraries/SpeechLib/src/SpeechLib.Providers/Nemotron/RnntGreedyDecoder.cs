using System.Diagnostics;

namespace SpeechLib.Nemotron;

/// <summary>
/// The two small networks of the RNN-T search as the greedy loop sees them. The prediction network
/// output depends only on (last token, committed LSTM state); the joint combines it with encoder frames.
/// </summary>
internal interface IRnntStepModel
{
    /// <summary>Runs the prediction network on <paramref name="token"/> and the committed state; the result stays pending.</summary>
    void Predict(int token);

    /// <summary>The pending LSTM state becomes the committed one (a non-blank token was emitted).</summary>
    void CommitPrediction();

    /// <summary>
    /// Log-probabilities over the vocabulary for encoder frames <paramref name="first"/> to <paramref name="first"/> +
    /// <paramref name="count"/> - 1 of the current chunk and the pending prediction output, in one run: <paramref name="count"/>
    /// rows of the vocabulary size, frame by frame. Valid until the next call.
    /// </summary>
    ReadOnlySpan<float> JoinFrames(int first, int count);

    /// <summary>Zeroes the committed LSTM state.</summary>
    void ResetState();
}

/// <summary>
/// Frame-synchronous greedy RNN-T search with the exact semantics of GenAI 0.16.0's <c>nemotron_speech</c> loop
/// (and NeMo's <c>GreedyRNNTInfer</c>): per encoder frame, repeat prediction + joint + argmax; a blank advances the
/// frame and leaves the decoder state and the last token alone; a non-blank is emitted, becomes the last token and
/// commits the LSTM state; after <c>max_symbols_per_step</c> symbols on one frame the frame advances without a
/// further blank check.
/// </summary>
/// <remarks>
/// <para>GenAI re-runs the prediction network on every frame, also after a blank. Its output is a pure function of
/// (last token, state), which only change on a non-blank, so this loop keeps the last output and runs the network
/// again only after an emitted token: exactly the same tokens with about 60 percent fewer prediction calls.</para>
/// <para>For the same reason the joint rows of all remaining frames of the chunk are known as soon as a prediction output
/// is: with <see cref="BatchedJoint"/> the joint runs once for frames [f, T) and the loop walks its rows until a token is
/// emitted, which makes the rest stale (the next prediction output re-runs it for [f, T)). Each row is the same
/// computation as a one-frame run (the joint's matrix products use pre-packed weights, so every row goes through the same
/// kernel whatever the batch size, and the log-softmax is per row): bit-identical rows, the same tokens, about a third of
/// the joint runs. Per-frame runs stay available for comparison and for a platform where that has not been verified.</para>
/// <para>The decoder state persists across chunks, pauses and flushes (a stream is one hypothesis) until <see cref="Reset"/>.</para>
/// </remarks>
internal sealed class RnntGreedyDecoder
{
    private readonly IRnntStepModel _model;
    private readonly int _blankId;
    private readonly int _maxSymbolsPerStep;

    private int _lastToken;
    private bool _predictionValid;
    private long _jointTicks;
    private long _predictionTicks;

    public RnntGreedyDecoder(IRnntStepModel model, int blankId, int maxSymbolsPerStep)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (maxSymbolsPerStep <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSymbolsPerStep));
        _model = model;
        _blankId = blankId;
        _maxSymbolsPerStep = maxSymbolsPerStep;
        _lastToken = blankId;
    }

    /// <summary>Subtracted from the blank log-probability before the argmax (<c>search.blank_penalty</c>).</summary>
    public float BlankPenalty { get; set; }

    /// <summary>
    /// One joint run for all remaining frames of the chunk per prediction output (true, default) instead of one run per
    /// frame and symbol like GenAI (false). Same tokens and scores; see the remarks.
    /// </summary>
    public bool BatchedJoint { get; set; } = true;

    /// <summary>Token the prediction network is conditioned on (the blank at the start of a stream).</summary>
    public int LastToken => _lastToken;

    /// <summary>Prediction-network runs since construction (diagnostics).</summary>
    public long PredictionCalls { get; private set; }

    /// <summary>Joint runs since construction (diagnostics).</summary>
    public long JointCalls { get; private set; }

    /// <summary>
    /// Encoder frames the joint has scored since construction (diagnostics): one per run frame by frame; with
    /// <see cref="BatchedJoint"/> every run scores the rest of the chunk, and rows after an emitted token are not used.
    /// </summary>
    public long JointFrames { get; private set; }

    /// <summary>Wall-clock time inside the joint runs since construction (diagnostics).</summary>
    public TimeSpan JointTime => Stopwatch.GetElapsedTime(0, _jointTicks);

    /// <summary>Wall-clock time inside the prediction-network runs since construction (diagnostics).</summary>
    public TimeSpan PredictionTime => Stopwatch.GetElapsedTime(0, _predictionTicks);

    /// <summary>
    /// Joint outputs that held nothing but NaN since construction (diagnostics). Such a frame is treated as a blank:
    /// it emits nothing and the search moves on. A non-zero count means NaN reached the encoder or the joint.
    /// </summary>
    public long NonFiniteJoints { get; private set; }

    /// <summary>Back to the start of a stream: the blank is the last token, the LSTM state is zero.</summary>
    public void Reset()
    {
        _lastToken = _blankId;
        _predictionValid = false;
        _model.ResetState();
    }

    /// <summary>
    /// Continues a stream whose committed LSTM state the step model was just switched to (another branch of the stream):
    /// <paramref name="lastToken"/> becomes the last token and the prediction is computed again before the next joint. Exact,
    /// because the prediction output is a function of (last token, committed state) only.
    /// </summary>
    public void Resume(int lastToken)
    {
        _lastToken = lastToken;
        _predictionValid = false;
    }

    /// <summary>
    /// Decodes the <paramref name="timeSteps"/> encoder frames of one chunk and appends the emitted tokens.
    /// </summary>
    /// <param name="timeSteps">Valid frames of the chunk's encoder output.</param>
    /// <param name="chunkIndex">Index of the chunk on the stream grid (counting dropped chunks).</param>
    /// <param name="framesPerChunk">Encoder frames per chunk, for the global frame number.</param>
    /// <param name="sink">Receives the tokens in emission order.</param>
    /// <returns>The number of tokens appended.</returns>
    public int DecodeChunk(int timeSteps, int chunkIndex, int framesPerChunk, List<NemotronToken> sink)
    {
        var emitted = 0;
        var frame = 0;
        var symbol = 0;
        var rows = ReadOnlySpan<float>.Empty;   // joint rows of frames [rowsFirst, timeSteps) for the current prediction output
        var rowsFirst = 0;
        var rowLength = 0;
        while (frame < timeSteps)
        {
            if (!_predictionValid)
            {
                var start = Stopwatch.GetTimestamp();
                _model.Predict(_lastToken);
                _predictionTicks += Stopwatch.GetTimestamp() - start;
                _predictionValid = true;
                PredictionCalls++;
            }

            ReadOnlySpan<float> logProbs;
            if (rows.IsEmpty)
            {
                var count = BatchedJoint ? timeSteps - frame : 1;
                var start = Stopwatch.GetTimestamp();
                logProbs = _model.JoinFrames(frame, count);
                _jointTicks += Stopwatch.GetTimestamp() - start;
                JointCalls++;
                JointFrames += count;
                if (BatchedJoint)
                {
                    rows = logProbs;
                    rowsFirst = frame;
                    rowLength = rows.Length / count;
                    logProbs = rows[..rowLength];
                }
            }
            else
            {
                logProbs = rows.Slice((frame - rowsFirst) * rowLength, rowLength);
            }
            var best = ArgMax(logProbs, _blankId, BlankPenalty);

            if (best == _blankId)
            {
                if (float.IsNaN(logProbs[best]))
                    NonFiniteJoints++;
                frame++;
                symbol = 0;
                continue;
            }

            sink.Add(new NemotronToken(best, chunkIndex, frame, symbol, (long)chunkIndex * framesPerChunk + frame, logProbs[best]));
            emitted++;

            _lastToken = best;
            _model.CommitPrediction();
            _predictionValid = false;
            rows = ReadOnlySpan<float>.Empty;   // scored against the prediction output this token replaces

            symbol++;
            if (symbol >= _maxSymbolsPerStep)
            {
                frame++;
                symbol = 0;
            }
        }
        return emitted;
    }

    /// <summary>
    /// Index of the largest score, the lowest index on ties (strict <c>&gt;</c>, like GenAI's <c>ArgMax</c>), with
    /// <paramref name="blankPenalty"/> subtracted from the blank's score only.
    /// </summary>
    /// <remarks>
    /// NaN never wins. GenAI's loop compares with <c>&gt;</c>, so a NaN in front of the row pinned the result to index 0
    /// (<c>&lt;unk&gt;</c>, a non-blank: ten of them per frame) for as long as NaN stayed in the encoder caches; here NaN
    /// entries are skipped and a row of nothing but NaN yields <paramref name="blankId"/>, which skips the frame. Rows
    /// without NaN give the same index as before.
    /// </remarks>
    internal static int ArgMax(ReadOnlySpan<float> values, int blankId, float blankPenalty)
    {
        if (values.IsEmpty)
            throw new ArgumentException("ArgMax needs at least one value.", nameof(values));

        var first = 0;
        while (first < values.Length && float.IsNaN(values[first]))
            first++;
        if (first == values.Length)
            return blankId;

        var best = first;
        if (blankPenalty == 0f)
        {
            var bestValue = values[first];
            for (var i = first + 1; i < values.Length; i++)
            {
                var v = values[i];
                if (v > bestValue)
                {
                    bestValue = v;
                    best = i;
                }
            }
            return best;
        }

        var bestScore = values[first] - (blankId == first ? blankPenalty : 0f);
        for (var i = first + 1; i < values.Length; i++)
        {
            var score = values[i] - (i == blankId ? blankPenalty : 0f);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }
}
