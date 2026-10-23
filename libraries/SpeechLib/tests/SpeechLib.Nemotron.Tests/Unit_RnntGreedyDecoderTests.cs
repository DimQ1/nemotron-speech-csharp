using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

public sealed class Unit_RnntGreedyDecoderTests
{
    private const int Vocab = 20;
    private const int Blank = 19;

    /// <summary>
    /// Step model with a scripted joint: <c>Choose(frame, lastToken, tokensEmitted)</c> names the token the joint prefers,
    /// everything else is log-probability -5. Records the prediction and commit traffic.
    /// </summary>
    private sealed class ScriptedModel(Func<int, int, int, int> choose) : IRnntStepModel
    {
        private readonly float[] _logProbs = new float[Vocab];
        private int _pendingToken = -1;
        private int _committedToken = Blank;
        private int _committedCount;
        private int _pendingCount;

        public List<int> PredictedTokens { get; } = new();
        public int Commits { get; private set; }
        public int Resets { get; private set; }
        public int JointRuns { get; private set; }
        public float BlankBonus { get; set; }

        public void Predict(int token)
        {
            PredictedTokens.Add(token);
            _pendingToken = token;
            _pendingCount = _committedCount + 1;
        }

        public void CommitPrediction()
        {
            Commits++;
            _committedToken = _pendingToken;
            _committedCount = _pendingCount;
        }

        public ReadOnlySpan<float> Join(int frame)
        {
            JointRuns++;
            Array.Fill(_logProbs, -5f);
            _logProbs[choose(frame, _pendingToken, _committedCount)] = -0.1f;
            _logProbs[Blank] += BlankBonus;
            return _logProbs;
        }

        public void ResetState()
        {
            Resets++;
            _committedToken = Blank;
            _committedCount = 0;
        }
    }

    private static RnntGreedyDecoder NewDecoder(ScriptedModel model, int maxSymbols = 10) => new(model, Blank, maxSymbols);

    [Fact]
    public void DecodeChunk_AllBlank_ShouldEmitNothingAndPredictOnce()
    {
        var model = new ScriptedModel((_, _, _) => Blank);
        var decoder = NewDecoder(model);
        var tokens = new List<NemotronToken>();

        var emitted = decoder.DecodeChunk(7, 0, 7, tokens);

        Assert.Equal(0, emitted);
        Assert.Empty(tokens);
        Assert.Equal([Blank], model.PredictedTokens);   // once per stream, not once per frame like GenAI
        Assert.Equal(7, model.JointRuns);
        Assert.Equal(0, model.Commits);
    }

    [Fact]
    public void DecodeChunk_TokenOnAFrame_ShouldRecordItsPositionAndRunThePredictionAgain()
    {
        // frame 2 emits token 5, then the joint prefers blank (the token count has changed)
        var model = new ScriptedModel((frame, _, emitted) => frame == 2 && emitted == 0 ? 5 : Blank);
        var decoder = NewDecoder(model);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(7, 3, 7, tokens);

        var token = Assert.Single(tokens);
        Assert.Equal(5, token.Id);
        Assert.Equal(3, token.Chunk);
        Assert.Equal(2, token.Frame);
        Assert.Equal(0, token.Symbol);
        Assert.Equal(3 * 7 + 2, token.GlobalFrame);
        Assert.Equal(-0.1f, token.LogProb);
        Assert.Equal([Blank, 5], model.PredictedTokens);  // initial, then conditioned on the emitted token
        Assert.Equal(1, model.Commits);
        Assert.Equal(7 + 1, model.JointRuns);             // the frame with the token is joined twice
        Assert.Equal(5, decoder.LastToken);
    }

    [Fact]
    public void DecodeChunk_BurstOnOneFrame_ShouldCountSymbolsWithinTheFrame()
    {
        // frame 1: tokens 3, 4, 6 then blank (emitted count 0..2 map to the tokens)
        int[] burst = [3, 4, 6];
        var model = new ScriptedModel((frame, _, emitted) => frame == 1 && emitted < 3 ? burst[emitted] : Blank);
        var decoder = NewDecoder(model);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(7, 0, 7, tokens);

        Assert.Equal([3, 4, 6], tokens.Select(t => t.Id));
        Assert.Equal([1, 1, 1], tokens.Select(t => t.Frame));
        Assert.Equal([0, 1, 2], tokens.Select(t => t.Symbol));
        Assert.Equal([Blank, 3, 4, 6], model.PredictedTokens);
        Assert.Equal(3, model.Commits);
    }

    [Fact]
    public void DecodeChunk_SymbolsPerFrameAreCapped_ShouldAdvanceWithoutFurtherBlankCheck()
    {
        var model = new ScriptedModel((_, _, _) => 7);   // never blank
        var decoder = NewDecoder(model, maxSymbols: 10);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(3, 0, 7, tokens);

        Assert.Equal(30, tokens.Count);                  // 10 on each of the 3 frames, then the chunk is done
        Assert.Equal([0, 1, 2], tokens.Select(t => t.Frame).Distinct());
        Assert.Equal(Enumerable.Range(0, 10), tokens.Where(t => t.Frame == 1).Select(t => t.Symbol));
        Assert.Equal(30, model.JointRuns);
    }

    [Fact]
    public void DecodeChunk_ShouldKeepTheStateAcrossChunksWithoutRepredicting()
    {
        var model = new ScriptedModel((frame, _, emitted) => emitted == 0 && frame == 6 ? 4 : Blank);
        var decoder = NewDecoder(model);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(7, 0, 7, tokens);           // token on the last frame
        decoder.DecodeChunk(7, 1, 7, tokens);           // ... the next chunk continues with the same hypothesis
        decoder.DecodeChunk(7, 2, 7, tokens);

        Assert.Equal([Blank, 4], model.PredictedTokens);  // one prediction per emitted token, none per chunk
        Assert.Equal(21 + 1, model.JointRuns);
        Assert.Equal(4, decoder.LastToken);
        Assert.Equal(1, model.Commits);
    }

    [Fact]
    public void DecodeChunk_BlankDoesNotTouchTheCommittedState()
    {
        var model = new ScriptedModel((_, _, _) => Blank);
        var decoder = NewDecoder(model);

        decoder.DecodeChunk(7, 0, 7, new List<NemotronToken>());

        Assert.Equal(0, model.Commits);
        Assert.Equal(Blank, decoder.LastToken);
    }

    [Fact]
    public void Reset_ShouldStartAFreshHypothesis()
    {
        var model = new ScriptedModel((frame, _, emitted) => emitted == 0 && frame == 0 ? 4 : Blank);
        var decoder = NewDecoder(model);
        var tokens = new List<NemotronToken>();
        decoder.DecodeChunk(2, 0, 7, tokens);
        Assert.Equal(4, decoder.LastToken);

        decoder.Reset();

        Assert.Equal(Blank, decoder.LastToken);
        Assert.Equal(1, model.Resets);
        decoder.DecodeChunk(2, 0, 7, tokens);
        Assert.Equal([Blank, 4, Blank, 4], model.PredictedTokens);  // after the reset the prediction starts from the blank again
    }

    [Theory]
    [InlineData(0f, 0)]      // blank -0.1 beats token 6 at -0.4
    [InlineData(0.2f, 0)]    // blank -0.3 still ahead
    [InlineData(0.5f, 1)]    // blank -0.6 loses: the token is emitted, then the blank wins again
    public void BlankPenalty_ShouldLetANearTieTokenWin(float penalty, int expectedTokens)
    {
        var decoder = new RnntGreedyDecoder(new CloseTokenModel(), Blank, 10) { BlankPenalty = penalty };
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(1, 0, 7, tokens);

        Assert.Equal(expectedTokens, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(6, t.Id));
    }

    // blank log-prob -0.1, token 6 at -0.4 (the blank wins by 0.3); after a token the blank is certain
    private sealed class CloseTokenModel : IRnntStepModel
    {
        private readonly float[] _logProbs = new float[Vocab];
        private bool _emitted;

        public void Predict(int token) { }
        public void CommitPrediction() => _emitted = true;
        public void ResetState() => _emitted = false;

        public ReadOnlySpan<float> Join(int frame)
        {
            Array.Fill(_logProbs, -9f);
            _logProbs[Blank] = _emitted ? 0f : -0.1f;
            _logProbs[6] = _emitted ? -9f : -0.4f;
            return _logProbs;
        }
    }

    [Fact]
    public void ArgMax_Ties_ShouldTakeTheLowestIndex()
    {
        float[] values = [0.1f, 0.9f, 0.3f, 0.9f, 0.9f, 0.2f];

        Assert.Equal(1, RnntGreedyDecoder.ArgMax(values, blankId: 5, blankPenalty: 0f));
        Assert.Equal(1, RnntGreedyDecoder.ArgMax(values, blankId: 5, blankPenalty: 0.25f));
        Assert.Equal(0, RnntGreedyDecoder.ArgMax([0.5f, 0.5f, 0.5f], blankId: 2, blankPenalty: 0f));
    }

    [Fact]
    public void ArgMax_BlankPenalty_ShouldOnlyLowerTheBlank()
    {
        float[] values = [0.1f, 0.5f, 0.9f, 0.8f];   // blank = 2 is the best by 0.1

        Assert.Equal(2, RnntGreedyDecoder.ArgMax(values, 2, 0f));
        Assert.Equal(2, RnntGreedyDecoder.ArgMax(values, 2, 0.05f));
        Assert.Equal(3, RnntGreedyDecoder.ArgMax(values, 2, 0.2f));
        Assert.Equal(0, RnntGreedyDecoder.ArgMax([0.9f, 0.5f], blankId: 0, blankPenalty: 0.1f - 0.1f));
        Assert.Equal(1, RnntGreedyDecoder.ArgMax([0.9f, 0.5f], blankId: 0, blankPenalty: 0.5f));
        Assert.Throws<ArgumentException>(() => RnntGreedyDecoder.ArgMax(ReadOnlySpan<float>.Empty, 0, 0f));
    }

    [Fact]
    public void ArgMax_WithNaN_ShouldNeverPickTheNaNAndYieldTheBlankForAnAllNaNRow()
    {
        const float nan = float.NaN;

        // GenAI's loop returned index 0 (<unk>) for a leading NaN, because nothing compares greater than NaN.
        Assert.Equal(2, RnntGreedyDecoder.ArgMax([nan, 0.1f, 0.9f, 0.5f], blankId: 3, blankPenalty: 0f));
        Assert.Equal(2, RnntGreedyDecoder.ArgMax([nan, 0.1f, 0.9f, 0.5f], blankId: 3, blankPenalty: 0.25f));
        Assert.Equal(3, RnntGreedyDecoder.ArgMax([nan, nan, 0.9f, 0.5f], blankId: 2, blankPenalty: 0.5f));   // the penalty still applies: 0.9 - 0.5 < 0.5
        Assert.Equal(1, RnntGreedyDecoder.ArgMax([0.2f, 0.9f, nan, 0.5f], blankId: 3, blankPenalty: 0f));    // a NaN further back never wins
        Assert.Equal(3, RnntGreedyDecoder.ArgMax([nan, nan, nan, nan], blankId: 3, blankPenalty: 0f));
        Assert.Equal(3, RnntGreedyDecoder.ArgMax([nan, nan, nan, nan], blankId: 3, blankPenalty: 0.25f));
        Assert.Equal(0, RnntGreedyDecoder.ArgMax([0.9f, 0.5f], blankId: 1, blankPenalty: 0f));               // rows without NaN are unchanged
    }

    // Joint whose output is all NaN on the frames in `nanFrames` (what a NaN in the encoder caches produces) and a certain blank elsewhere.
    private sealed class NanModel(params int[] nanFrames) : IRnntStepModel
    {
        private readonly float[] _row = new float[Vocab];

        public void Predict(int token) { }
        public void CommitPrediction() { }
        public void ResetState() { }

        public ReadOnlySpan<float> Join(int frame)
        {
            Array.Fill(_row, nanFrames.Contains(frame) ? float.NaN : -9f);
            if (!nanFrames.Contains(frame))
                _row[Blank] = -0.01f;
            return _row;
        }
    }

    [Fact]
    public void DecodeChunk_FramesOfNaN_ShouldEmitNothingAdvanceAndBeCounted()
    {
        var decoder = new RnntGreedyDecoder(new NanModel(1, 2, 6), Blank, 10);
        var tokens = new List<NemotronToken>();

        var emitted = decoder.DecodeChunk(7, 0, 7, tokens);

        Assert.Equal(0, emitted);                      // no <unk> burst: ten non-blank tokens per frame before
        Assert.Empty(tokens);
        Assert.Equal(3, decoder.NonFiniteJoints);
        Assert.Equal(7, decoder.JointCalls);           // every frame was visited exactly once
        Assert.Equal(Blank, decoder.LastToken);
    }

    [Fact]
    public void Constructor_ShouldRejectBadArguments()
    {
        var model = new ScriptedModel((_, _, _) => Blank);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RnntGreedyDecoder(model, Blank, 0));
        Assert.Throws<ArgumentNullException>(() => new RnntGreedyDecoder(null!, Blank, 10));
    }
}
