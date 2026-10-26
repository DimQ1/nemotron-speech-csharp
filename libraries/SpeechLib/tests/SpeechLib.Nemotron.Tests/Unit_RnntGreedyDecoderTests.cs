using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

public sealed class Unit_RnntGreedyDecoderTests
{
    private const int Vocab = 20;
    private const int Blank = 19;
    private const int MaxFrames = 16;

    /// <summary>
    /// Step model with a scripted joint: <c>Choose(frame, lastToken, tokensEmitted)</c> names the token the joint prefers,
    /// everything else is log-probability -5. Records the prediction, commit and joint traffic.
    /// </summary>
    private sealed class ScriptedModel(Func<int, int, int, int> choose) : IRnntStepModel
    {
        private readonly float[] _rows = new float[MaxFrames * Vocab];
        private int _pendingToken = -1;
        private int _committedToken = Blank;
        private int _committedCount;
        private int _pendingCount;

        public List<int> PredictedTokens { get; } = new();
        public List<(int First, int Count)> JointBatches { get; } = new();
        public int Commits { get; private set; }
        public int Resets { get; private set; }
        public int JointRuns => JointBatches.Count;
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

        public ReadOnlySpan<float> JoinFrames(int first, int count)
        {
            JointBatches.Add((first, count));
            for (var i = 0; i < count; i++)
            {
                var row = _rows.AsSpan(i * Vocab, Vocab);
                row.Fill(-5f);
                row[choose(first + i, _pendingToken, _committedCount)] = -0.1f;
                row[Blank] += BlankBonus;
            }
            return _rows.AsSpan(0, count * Vocab);
        }

        public void ResetState()
        {
            Resets++;
            _committedToken = Blank;
            _committedCount = 0;
        }
    }

    private static RnntGreedyDecoder NewDecoder(ScriptedModel model, int maxSymbols = 10, bool batched = true) =>
        new(model, Blank, maxSymbols) { BatchedJoint = batched };

    [Theory]
    [InlineData(false, 7)]   // one run per frame
    [InlineData(true, 1)]    // one run for the whole chunk
    public void DecodeChunk_AllBlank_ShouldEmitNothingAndPredictOnce(bool batched, int jointRuns)
    {
        var model = new ScriptedModel((_, _, _) => Blank);
        var decoder = NewDecoder(model, batched: batched);
        var tokens = new List<NemotronToken>();

        var emitted = decoder.DecodeChunk(7, 0, 7, tokens);

        Assert.Equal(0, emitted);
        Assert.Empty(tokens);
        Assert.Equal([Blank], model.PredictedTokens);   // once per stream, not once per frame like GenAI
        Assert.Equal(jointRuns, model.JointRuns);
        Assert.Equal(jointRuns, decoder.JointCalls);
        Assert.Equal(7, decoder.JointFrames);
        Assert.Equal(0, model.Commits);
    }

    [Theory]
    [InlineData(false, 7 + 1, 7 + 1)]   // the frame with the token is joined twice
    [InlineData(true, 2, 7 + 5)]        // frames 0..6, then 2..6 against the new prediction output
    public void DecodeChunk_TokenOnAFrame_ShouldRecordItsPositionAndRunThePredictionAgain(bool batched, int jointRuns, int jointFrames)
    {
        // frame 2 emits token 5, then the joint prefers blank (the token count has changed)
        var model = new ScriptedModel((frame, _, emitted) => frame == 2 && emitted == 0 ? 5 : Blank);
        var decoder = NewDecoder(model, batched: batched);
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
        Assert.Equal(jointRuns, model.JointRuns);
        Assert.Equal(jointFrames, decoder.JointFrames);
        Assert.Equal(5, decoder.LastToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeChunk_BurstOnOneFrame_ShouldCountSymbolsWithinTheFrame(bool batched)
    {
        // frame 1: tokens 3, 4, 6 then blank (emitted count 0..2 map to the tokens)
        int[] burst = [3, 4, 6];
        var model = new ScriptedModel((frame, _, emitted) => frame == 1 && emitted < 3 ? burst[emitted] : Blank);
        var decoder = NewDecoder(model, batched: batched);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(7, 0, 7, tokens);

        Assert.Equal([3, 4, 6], tokens.Select(t => t.Id));
        Assert.Equal([1, 1, 1], tokens.Select(t => t.Frame));
        Assert.Equal([0, 1, 2], tokens.Select(t => t.Symbol));
        Assert.Equal([Blank, 3, 4, 6], model.PredictedTokens);
        Assert.Equal(3, model.Commits);
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 10 * 3 + 10 * 2 + 10 * 1)]   // every token re-runs the rest of the chunk
    public void DecodeChunk_SymbolsPerFrameAreCapped_ShouldAdvanceWithoutFurtherBlankCheck(bool batched, int jointFrames)
    {
        var model = new ScriptedModel((_, _, _) => 7);   // never blank
        var decoder = NewDecoder(model, maxSymbols: 10, batched: batched);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(3, 0, 7, tokens);

        Assert.Equal(30, tokens.Count);                  // 10 on each of the 3 frames, then the chunk is done
        Assert.Equal([0, 1, 2], tokens.Select(t => t.Frame).Distinct());
        Assert.Equal(Enumerable.Range(0, 10), tokens.Where(t => t.Frame == 1).Select(t => t.Symbol));
        Assert.Equal(30, model.JointRuns);
        Assert.Equal(jointFrames, decoder.JointFrames);
    }

    [Theory]
    [InlineData(false, 21 + 1)]
    [InlineData(true, 4)]        // [0, 7) and [6, 7) in the first chunk, one run for each further chunk
    public void DecodeChunk_ShouldKeepTheStateAcrossChunksWithoutRepredicting(bool batched, int jointRuns)
    {
        var model = new ScriptedModel((frame, _, emitted) => emitted == 0 && frame == 6 ? 4 : Blank);
        var decoder = NewDecoder(model, batched: batched);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(7, 0, 7, tokens);           // token on the last frame
        decoder.DecodeChunk(7, 1, 7, tokens);           // ... the next chunk continues with the same hypothesis
        decoder.DecodeChunk(7, 2, 7, tokens);

        Assert.Equal([Blank, 4], model.PredictedTokens);  // one prediction per emitted token, none per chunk
        Assert.Equal(jointRuns, model.JointRuns);
        Assert.Equal(4, decoder.LastToken);
        Assert.Equal(1, model.Commits);
    }

    [Fact]
    public void DecodeChunk_Batched_ShouldRunTheJointAgainOnlyAfterAnEmittedToken()
    {
        // token 5 on frame 2, then token 6 on frame 4
        var model = new ScriptedModel((frame, _, emitted) => (frame, emitted) switch
        {
            (2, 0) => 5,
            (4, 1) => 6,
            _ => Blank,
        });
        var decoder = NewDecoder(model);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(7, 0, 7, tokens);

        Assert.Equal([5, 6], tokens.Select(t => t.Id));
        Assert.Equal([2, 4], tokens.Select(t => t.Frame));
        Assert.Equal([(0, 7), (2, 5), (4, 3)], model.JointBatches);   // each batch covers the rest of the chunk
        Assert.Equal(15, decoder.JointFrames);
    }

    [Fact]
    public void DecodeChunk_Batched_ShouldNeverJoinPastTheValidFramesOrReuseRowsOfAnotherChunk()
    {
        var model = new ScriptedModel((_, _, _) => Blank);
        var decoder = NewDecoder(model);
        var tokens = new List<NemotronToken>();

        decoder.DecodeChunk(4, 0, 7, tokens);           // an encoder output with fewer valid frames than the chunk has
        decoder.DecodeChunk(7, 1, 7, tokens);           // same prediction output, but the frames are the next chunk's

        Assert.Equal([(0, 4), (0, 7)], model.JointBatches);
        Assert.Equal([Blank], model.PredictedTokens);
    }

    [Fact]
    public void BatchedJoint_ShouldBeTheDefault()
    {
        Assert.True(new RnntGreedyDecoder(new ScriptedModel((_, _, _) => Blank), Blank, 10).BatchedJoint);
    }

    /// <summary>
    /// A joint whose rows are a pseudo-random function of (chunk, frame, conditioning token, tokens committed), like the real
    /// joint a function of (encoder frame, prediction output): a row is the same whichever batch it is computed in. Every
    /// row has one candidate token that beats the blank about half of the time, some rows are all NaN, some have an exact tie.
    /// </summary>
    private sealed class HashedModel(int seed) : IRnntStepModel
    {
        private readonly float[] _rows = new float[MaxFrames * Vocab];
        private int _pendingToken = -1;
        private int _committedCount;
        private int _pendingCount;

        public int Chunk { get; set; }

        public void Predict(int token)
        {
            _pendingToken = token;
            _pendingCount = _committedCount + 1;
        }

        public void CommitPrediction() => _committedCount = _pendingCount;

        public void ResetState() => _committedCount = 0;

        public ReadOnlySpan<float> JoinFrames(int first, int count)
        {
            for (var i = 0; i < count; i++)
                FillRow(first + i, _rows.AsSpan(i * Vocab, Vocab));
            return _rows.AsSpan(0, count * Vocab);
        }

        private void FillRow(int frame, Span<float> row)
        {
            var rng = new Random(unchecked(seed * 1_000_003 + Chunk * 10_007 + frame * 1_009 + _pendingToken * 101 + _committedCount));
            if (rng.Next(25) == 0)
            {
                row.Fill(float.NaN);
                return;
            }
            for (var v = 0; v < row.Length; v++)
                row[v] = -1f - 4f * rng.NextSingle();
            row[Blank] = -1.2f * rng.NextSingle();
            var candidate = rng.Next(Blank);
            row[candidate] = rng.Next(10) == 0 ? row[Blank] : -1.2f * rng.NextSingle();
            if (rng.Next(20) == 0)
                row[rng.Next(Vocab)] = float.NaN;
        }
    }

    [Fact]
    public void DecodeChunk_BatchedAndFrameByFrame_ShouldEmitTheSameTokensWithTheSameScores()
    {
        var totalTokens = 0;
        for (var seed = 1; seed <= 300; seed++)
        {
            var settings = new Random(seed);
            var framesPerChunk = settings.Next(1, MaxFrames + 1);
            var maxSymbols = settings.Next(1, 5);
            var penalty = settings.Next(3) == 0 ? 0.3f : 0f;
            var chunks = Enumerable.Range(0, settings.Next(1, 6)).Select(_ => settings.Next(1, framesPerChunk + 1)).ToArray();

            var frameByFrame = Decode(new HashedModel(seed), batched: false);
            var batched = Decode(new HashedModel(seed), batched: true);

            Assert.Equal(frameByFrame.Tokens, batched.Tokens);
            Assert.Equal(frameByFrame.Tokens.Select(t => BitConverter.SingleToInt32Bits(t.LogProb)), batched.Tokens.Select(t => BitConverter.SingleToInt32Bits(t.LogProb)));
            Assert.Equal(frameByFrame.Decoder.LastToken, batched.Decoder.LastToken);
            Assert.Equal(frameByFrame.Decoder.PredictionCalls, batched.Decoder.PredictionCalls);
            Assert.Equal(frameByFrame.Decoder.NonFiniteJoints, batched.Decoder.NonFiniteJoints);
            Assert.True(batched.Decoder.JointCalls <= frameByFrame.Decoder.JointCalls, $"seed {seed}: more joint runs batched");
            totalTokens += batched.Tokens.Count;

            (List<NemotronToken> Tokens, RnntGreedyDecoder Decoder) Decode(HashedModel model, bool batched)
            {
                var decoder = new RnntGreedyDecoder(model, Blank, maxSymbols) { BlankPenalty = penalty, BatchedJoint = batched };
                var tokens = new List<NemotronToken>();
                for (var c = 0; c < chunks.Length; c++)
                {
                    model.Chunk = c;
                    decoder.DecodeChunk(chunks[c], c, framesPerChunk, tokens);
                }
                return (tokens, decoder);
            }
        }
        Assert.InRange(totalTokens, 500, int.MaxValue);   // the scripts emit enough to exercise re-batching
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
        private readonly float[] _rows = new float[MaxFrames * Vocab];
        private bool _emitted;

        public void Predict(int token) { }
        public void CommitPrediction() => _emitted = true;
        public void ResetState() => _emitted = false;

        public ReadOnlySpan<float> JoinFrames(int first, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var row = _rows.AsSpan(i * Vocab, Vocab);
                row.Fill(-9f);
                row[Blank] = _emitted ? 0f : -0.1f;
                row[6] = _emitted ? -9f : -0.4f;
            }
            return _rows.AsSpan(0, count * Vocab);
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
        private readonly float[] _rows = new float[MaxFrames * Vocab];

        public void Predict(int token) { }
        public void CommitPrediction() { }
        public void ResetState() { }

        public ReadOnlySpan<float> JoinFrames(int first, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var row = _rows.AsSpan(i * Vocab, Vocab);
                var nan = nanFrames.Contains(first + i);
                row.Fill(nan ? float.NaN : -9f);
                if (!nan)
                    row[Blank] = -0.01f;
            }
            return _rows.AsSpan(0, count * Vocab);
        }
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 1)]
    public void DecodeChunk_FramesOfNaN_ShouldEmitNothingAdvanceAndBeCounted(bool batched, int jointRuns)
    {
        var decoder = new RnntGreedyDecoder(new NanModel(1, 2, 6), Blank, 10) { BatchedJoint = batched };
        var tokens = new List<NemotronToken>();

        var emitted = decoder.DecodeChunk(7, 0, 7, tokens);

        Assert.Equal(0, emitted);                      // no <unk> burst: ten non-blank tokens per frame before
        Assert.Empty(tokens);
        Assert.Equal(3, decoder.NonFiniteJoints);
        Assert.Equal(jointRuns, decoder.JointCalls);
        Assert.Equal(7, decoder.JointFrames);          // every frame was scored exactly once
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
