namespace SpeechLib.Nemotron;

/// <summary>
/// The two small networks of the RNN-T search as the greedy loop sees them. The prediction network
/// output depends only on (last token, committed LSTM state); the joint combines it with one encoder frame.
/// </summary>
internal interface IRnntStepModel
{
    /// <summary>Runs the prediction network on <paramref name="token"/> and the committed state; the result stays pending.</summary>
    void Predict(int token);

    /// <summary>The pending LSTM state becomes the committed one (a non-blank token was emitted).</summary>
    void CommitPrediction();

    /// <summary>Log-probabilities over the vocabulary for encoder frame <paramref name="frame"/> of the current chunk and the pending prediction output.</summary>
    ReadOnlySpan<float> Join(int frame);

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
/// GenAI re-runs the prediction network on every frame, also after a blank. Its output is a pure function of
/// (last token, state), which only change on a non-blank, so this loop keeps the last output and runs the network
/// again only after an emitted token: exactly the same tokens with about 60 percent fewer prediction calls.
/// The decoder state persists across chunks, pauses and flushes (a stream is one hypothesis) until <see cref="Reset"/>.
/// </remarks>
internal sealed class RnntGreedyDecoder
{
    private readonly IRnntStepModel _model;
    private readonly int _blankId;
    private readonly int _maxSymbolsPerStep;

    private int _lastToken;
    private bool _predictionValid;

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

    /// <summary>Token the prediction network is conditioned on (the blank at the start of a stream).</summary>
    public int LastToken => _lastToken;

    /// <summary>Prediction-network runs since construction (diagnostics).</summary>
    public long PredictionCalls { get; private set; }

    /// <summary>Joint runs since construction (diagnostics).</summary>
    public long JointCalls { get; private set; }

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
        while (frame < timeSteps)
        {
            if (!_predictionValid)
            {
                _model.Predict(_lastToken);
                _predictionValid = true;
                PredictionCalls++;
            }

            var logProbs = _model.Join(frame);
            JointCalls++;
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
