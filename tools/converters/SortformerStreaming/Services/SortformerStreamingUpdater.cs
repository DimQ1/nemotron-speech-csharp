using System.Diagnostics;
using SortformerStreaming.Models;

namespace SortformerStreaming.Services;

/// <summary>
/// Pure C# port of <c>SortformerModules.streaming_update</c> and <c>_compress_spkcache</c>.
/// Inference-only: no training-time speaker permutation or random noise.
/// </summary>
public sealed class SortformerStreamingUpdater
{
    private readonly SortformerStreamingConfig _cfg;

    public SortformerStreamingUpdater(SortformerStreamingConfig? cfg = null)
    {
        _cfg = cfg ?? new SortformerStreamingConfig();
    }

    /// <summary>
    /// Creates a fresh streaming state for a batch of sessions.
    /// </summary>
    public SortformerStreamingState CreateState(int batchSize, int embDim)
    {
        var state = new SortformerStreamingState
        {
            Spkcache = new float[batchSize][][],
            SpkcacheLengths = new int[batchSize],
            SpkcachePreds = new float[batchSize][][],
            Fifo = new float[batchSize][][],
            FifoLengths = new int[batchSize],
            FifoPreds = new float[batchSize][][],
            SpkPerm = null,
            MeanSilEmb = new float[batchSize][],
            NSilFrames = new int[batchSize],
        };

        for (int b = 0; b < batchSize; b++)
        {
            state.Spkcache[b] = Array.Empty<float[]>();
            state.SpkcachePreds[b] = Array.Empty<float[]>();
            state.Fifo[b] = Array.Empty<float[]>();
            state.FifoPreds[b] = Array.Empty<float[]>();
            state.MeanSilEmb[b] = new float[embDim];
        }

        return state;
    }

    /// <summary>
    /// Update speaker cache and FIFO with a new chunk of embeddings and the corresponding predictions.
    /// </summary>
    /// <param name="state">Current streaming state.</param>
    /// <param name="chunk">Chunk embeddings. Shape: (batch, lc+chunk_len+rc, emb_dim).</param>
    /// <param name="allPreds">Predictions for [spkcache + fifo + chunk]. Shape: (batch, spkcache_len+fifo_len+lc+chunk_len+rc, n_spk).</param>
    /// <param name="lc">Left context length.</param>
    /// <param name="rc">Right context length.</param>
    /// <param name="catLengths">
    /// Optional valid (non-padded) lengths of the [spkcache + fifo + chunk] sequence, per batch.
    /// When provided, frames at index >= catLengths[b] are zeroed before the update
    /// (mirrors NeMo <c>apply_mask_to_preds</c>). Pass null when there is no padding.
    /// </param>
    /// <returns>Updated state and predictions for the chunk. Chunk preds shape: (batch, chunk_len, n_spk).</returns>
    public (SortformerStreamingState state, float[][][] chunkPreds) StreamingUpdate(
        SortformerStreamingState state,
        float[][][] chunk,
        float[][][] allPreds,
        int lc = 0,
        int rc = 0,
        int[]? catLengths = null)
    {
        int batchSize = chunk.Length;
        int chunkLen = chunk[0].Length - lc - rc;
        // Python computes spkcache_len and fifo_len from the actual state tensors, not the config.
        int spkcacheLen = state.Spkcache[0].Length;

        // Mirror NeMo apply_mask_to_preds: zero padded frames beyond the valid length.
        if (catLengths != null)
        {
            for (int b = 0; b < batchSize; b++)
            {
                int valid = Math.Min(catLengths[b], allPreds[b].Length);
                for (int f = valid; f < allPreds[b].Length; f++)
                    Array.Clear(allPreds[b][f], 0, allPreds[b][f].Length);
            }
        }
        int currentFifoLen = state.Fifo[0].Length;

        // In inference mode spk_perm is null, so we skip the inverse permutation step.

        // fifo_preds = allPreds[:, spkcache_len : spkcache_len + fifo_len]
        state.FifoPreds = SliceFrames(allPreds, spkcacheLen, spkcacheLen + currentFifoLen);

        // chunk = chunk[:, lc : chunk_len + lc]
        float[][][] chunkCore = SliceFrames(chunk, lc, lc + chunkLen);

        // chunk_preds = allPreds[:, spkcache_len + fifo_len + lc : spkcache_len + fifo_len + chunk_len + lc]
        int chunkPredStart = spkcacheLen + currentFifoLen + lc;
        float[][][] chunkPreds = SliceFrames(allPreds, chunkPredStart, chunkPredStart + chunkLen);

        // Append chunk to FIFO
        state.Fifo = ConcatenateFrames(state.Fifo, chunkCore);
        state.FifoPreds = ConcatenateFrames(state.FifoPreds, chunkPreds);

        // If FIFO exceeds target length, pop oldest frames into spkcache
        int actualFifoLen = currentFifoLen + chunkLen;
        if (actualFifoLen > _cfg.FifoLen)
        {
            int popOutLen = _cfg.SpkcacheUpdatePeriod;
            popOutLen = Math.Max(popOutLen, chunkLen - _cfg.FifoLen + currentFifoLen);
            popOutLen = Math.Min(popOutLen, actualFifoLen);

            // Pop oldest embeddings and their predictions
            float[][][] popOutEmbs = SliceFrames(state.Fifo, 0, popOutLen);
            float[][][] popOutPreds = SliceFrames(state.FifoPreds, 0, popOutLen);
            state.Fifo = SliceFrames(state.Fifo, popOutLen, state.Fifo[0].Length);
            state.FifoPreds = SliceFrames(state.FifoPreds, popOutLen, state.FifoPreds[0].Length);

            // Update silence profile from popped-out frames
            (state.MeanSilEmb, state.NSilFrames) = GetSilenceProfile(
                state.MeanSilEmb, state.NSilFrames, popOutEmbs, popOutPreds);

            // Append popped-out embeddings to spkcache
            state.Spkcache = ConcatenateFrames(state.Spkcache, popOutEmbs);
            state.SpkcachePreds = ConcatenateFrames(state.SpkcachePreds, popOutPreds);

            if (state.Spkcache[0].Length > _cfg.SpkcacheLen)
            {
                if (state.SpkcachePreds[0].Length == 0)
                {
                    // First update of speaker cache: take the initial spkcache_len predictions
                    // and the popped-out predictions. Use the actual spkcache length before compression.
                    float[][][] initialPreds = SliceFrames(allPreds, 0, spkcacheLen);
                    state.SpkcachePreds = ConcatenateFrames(initialPreds, popOutPreds);
                }

                (state.Spkcache, state.SpkcachePreds, _) = CompressSpkcache(
                    state.Spkcache, state.SpkcachePreds, state.MeanSilEmb, permuteSpk: false);
            }
        }

        if (SortformerLogging.Log)
        {
            Debug.WriteLine(
                $"spkcache: {state.Spkcache[0].Length}, fifo: {state.Fifo[0].Length}, " +
                $"chunk: {chunkCore[0].Length}, chunk_preds: {chunkPreds[0].Length}");
        }

        return (state, chunkPreds);
    }

    /// <summary>
    /// Compress speaker cache to <see cref="SortformerStreamingConfig.SpkcacheLen"/> most important frames.
    /// </summary>
    public (float[][][] emb, float[][][] preds, int[][]? spkPerm) CompressSpkcache(
        float[][][] embSeq,
        float[][][] preds,
        float[][] meanSilEmb,
        bool permuteSpk = false)
    {
        int batchSize = preds.Length;
        int nFrames = preds[0].Length;
        int nSpk = _cfg.NSpk;
        int embDim = embSeq[0][0].Length;


        int spkcacheLenPerSpk = _cfg.SpkcacheLen / nSpk - _cfg.SpkcacheSilFramesPerSpk;
        int strongBoostPerSpk = (int)Math.Floor(spkcacheLenPerSpk * _cfg.StrongBoostRate);
        int weakBoostPerSpk = (int)Math.Floor(spkcacheLenPerSpk * _cfg.WeakBoostRate);
        int minPosScoresPerSpk = (int)Math.Floor(spkcacheLenPerSpk * _cfg.MinPosScoresRate);

        float[][][] scores = GetLogPredScores(preds);
        scores = DisableLowScores(preds, scores, minPosScoresPerSpk);

        int[][]? spkPerm = null;
        if (permuteSpk)
        {
            // Training-only path kept for parity but not used in inference.
            int[] maxPermIndex = GetMaxPermIndex(scores);
            (scores, spkPerm) = PermuteSpeakers(scores, maxPermIndex);
        }

        if (_cfg.ScoresBoostLatest > 0)
        {
            scores = BoostLatest(scores, _cfg.ScoresBoostLatest, _cfg.SpkcacheLen);
        }

        if (_cfg.ScoresAddRnd > 0)
        {
            var rnd = new Random();
            for (int b = 0; b < batchSize; b++)
                for (int f = 0; f < nFrames; f++)
                    for (int s = 0; s < nSpk; s++)
                        scores[b][f][s] += (float)rnd.NextDouble() * _cfg.ScoresAddRnd;
        }

        scores = BoostTopkScores(scores, strongBoostPerSpk, scaleFactor: 2f);
        scores = BoostTopkScores(scores, weakBoostPerSpk, scaleFactor: 1f);

        if (_cfg.SpkcacheSilFramesPerSpk > 0)
        {
            scores = AddSilencePadding(scores, _cfg.SpkcacheSilFramesPerSpk, nSpk);
        }

        (int[][] topkIndices, bool[][] isDisabled) = GetTopkIndices(scores, _cfg.SpkcacheLen, _cfg.SpkcacheSilFramesPerSpk, _cfg.MaxIndex);

        (float[][][] spkcache, float[][][] spkcachePreds) = GatherSpkcacheAndPreds(
            embSeq, preds, topkIndices, isDisabled, meanSilEmb);

        return (spkcache, spkcachePreds, spkPerm);
    }

    #region Silence profile

    private (float[][] meanSilEmb, int[] nSilFrames) GetSilenceProfile(
        float[][] meanSilEmb,
        int[] nSilFrames,
        float[][][] embSeq,
        float[][][] preds)
    {
        int batchSize = embSeq.Length;
        int embDim = embSeq[0][0].Length;

        var updMeanSilEmb = new float[batchSize][];
        var updNSilFrames = new int[batchSize];

        for (int b = 0; b < batchSize; b++)
        {
            int nFrames = embSeq[b].Length;
            int silCount = 0;
            float[] silEmbSum = new float[embDim];
            for (int f = 0; f < nFrames; f++)
            {
                float sumPreds = Sum(preds[b][f]);
                if (sumPreds < _cfg.SilThreshold)
                {
                    silCount++;
                    for (int d = 0; d < embDim; d++)
                        silEmbSum[d] += embSeq[b][f][d];
                }
            }

            if (silCount == 0)
            {
                updMeanSilEmb[b] = meanSilEmb[b] ?? new float[embDim];
                updNSilFrames[b] = nSilFrames[b];
            }
            else
            {
                int total = nSilFrames[b] + silCount;
                float[] oldSilEmbSum = meanSilEmb[b] != null
                    ? meanSilEmb[b].Select(x => x * nSilFrames[b]).ToArray()
                    : new float[embDim];

                float[] totalSilSum = new float[embDim];
                for (int d = 0; d < embDim; d++)
                    totalSilSum[d] = oldSilEmbSum[d] + silEmbSum[d];

                float[] mean = new float[embDim];
                for (int d = 0; d < embDim; d++)
                    mean[d] = totalSilSum[d] / Math.Max(1, total);

                updMeanSilEmb[b] = mean;
                updNSilFrames[b] = total;
            }
        }

        return (updMeanSilEmb, updNSilFrames);
    }

    #endregion

    #region Scores and helpers

    private float[][][] GetLogPredScores(float[][][] preds)
    {
        int batchSize = preds.Length;
        int nFrames = preds[0].Length;
        int nSpk = _cfg.NSpk;

        var scores = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            scores[b] = new float[nFrames][];
            for (int f = 0; f < nFrames; f++)
            {
                scores[b][f] = new float[nSpk];
                float log1ProbsSum = 0f;
                for (int s = 0; s < nSpk; s++)
                {
                    float p = Clamp(preds[b][f][s], _cfg.PredScoreThreshold, 1f - _cfg.PredScoreThreshold);
                    log1ProbsSum += MathF.Log(1f - p);
                }

                for (int s = 0; s < nSpk; s++)
                {
                    float p = Clamp(preds[b][f][s], _cfg.PredScoreThreshold, 1f - _cfg.PredScoreThreshold);
                    float logP = MathF.Log(p);
                    float log1P = MathF.Log(1f - p);
                    scores[b][f][s] = logP - log1P + log1ProbsSum - MathF.Log(0.5f);
                }
            }
        }
        return scores;
    }

    private float[][][] DisableLowScores(float[][][] preds, float[][][] scores, int minPosScoresPerSpk)
    {
        int batchSize = preds.Length;
        int nFrames = preds[0].Length;
        int nSpk = _cfg.NSpk;

        var result = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            result[b] = new float[nFrames][];
            int[] posCountPerSpk = new int[nSpk];
            for (int f = 0; f < nFrames; f++)
                for (int s = 0; s < nSpk; s++)
                    if (scores[b][f][s] > 0 && preds[b][f][s] > 0.5f)
                        posCountPerSpk[s]++;

            for (int f = 0; f < nFrames; f++)
            {
                result[b][f] = new float[nSpk];
                for (int s = 0; s < nSpk; s++)
                {
                    bool isSpeech = preds[b][f][s] > 0.5f;
                    bool isPos = scores[b][f][s] > 0;
                    bool shouldDisable = !isSpeech || (!isPos && posCountPerSpk[s] >= minPosScoresPerSpk);
                    result[b][f][s] = shouldDisable ? float.NegativeInfinity : scores[b][f][s];
                }
            }
        }
        return result;
    }

    private int[] GetMaxPermIndex(float[][][] scores)
    {
        int batchSize = scores.Length;
        int nSpk = _cfg.NSpk;
        var maxPermIndex = Enumerable.Repeat(nSpk, batchSize).ToArray();

        for (int b = 0; b < batchSize; b++)
        {
            for (int s = 0; s < nSpk; s++)
            {
                bool anyPos = false;
                for (int f = 0; f < scores[b].Length; f++)
                {
                    if (scores[b][f][s] > 0)
                    {
                        anyPos = true;
                        break;
                    }
                }
                if (!anyPos)
                {
                    maxPermIndex[b] = s;
                    break;
                }
            }
        }
        return maxPermIndex;
    }

    private (float[][][] scores, int[][] spkPerm) PermuteSpeakers(float[][][] scores, int[] maxPermIndex)
    {
        int batchSize = scores.Length;
        int nFrames = scores[0].Length;
        int nSpk = _cfg.NSpk;

        var permutedScores = new float[batchSize][][];
        var spkPerm = new int[batchSize][];
        var rnd = new Random();

        for (int b = 0; b < batchSize; b++)
        {
            int k = maxPermIndex[b];
            var perm = Enumerable.Range(0, k).OrderBy(_ => rnd.Next())
                .Concat(Enumerable.Range(k, nSpk - k))
                .ToArray();
            spkPerm[b] = perm;

            permutedScores[b] = new float[nFrames][];
            for (int f = 0; f < nFrames; f++)
            {
                permutedScores[b][f] = new float[nSpk];
                for (int s = 0; s < nSpk; s++)
                    permutedScores[b][f][s] = scores[b][f][perm[s]];
            }
        }

        return (permutedScores, spkPerm);
    }

    private float[][][] BoostLatest(float[][][] scores, float boost, int spkcacheLen)
    {
        int batchSize = scores.Length;
        int nFrames = scores[0].Length;
        int nSpk = _cfg.NSpk;

        var result = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            result[b] = new float[nFrames][];
            for (int f = 0; f < nFrames; f++)
            {
                result[b][f] = new float[nSpk];
                float add = f >= spkcacheLen ? boost : 0f;
                for (int s = 0; s < nSpk; s++)
                    result[b][f][s] = scores[b][f][s] + add;
            }
        }
        return result;
    }

    private float[][][] BoostTopkScores(float[][][] scores, int nBoostPerSpk, float scaleFactor = 1f, float offset = 0.5f)
    {
        int batchSize = scores.Length;
        int nFrames = scores[0].Length;
        int nSpk = _cfg.NSpk;
        float delta = -scaleFactor * MathF.Log(offset);

        var result = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            result[b] = new float[nFrames][];
            for (int f = 0; f < nFrames; f++)
            {
                result[b][f] = new float[nSpk];
                for (int s = 0; s < nSpk; s++)
                    result[b][f][s] = scores[b][f][s];
            }

            for (int s = 0; s < nSpk; s++)
            {
                var indexed = Enumerable.Range(0, nFrames)
                    .Select(f => (f, v: result[b][f][s]))
                    .Where(x => !float.IsNegativeInfinity(x.v))
                    .OrderByDescending(x => x.v)
                    .Take(nBoostPerSpk)
                    .Select(x => x.f)
                    .ToList();
                foreach (int f in indexed)
                    result[b][f][s] += delta;
            }
        }
        return result;
    }

    private float[][][] AddSilencePadding(float[][][] scores, int silFramesPerSpk, int nSpk)
    {
        int batchSize = scores.Length;
        int nFrames = scores[0].Length;
        var result = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            result[b] = new float[nFrames + silFramesPerSpk][];
            for (int f = 0; f < nFrames; f++)
            {
                result[b][f] = new float[nSpk];
                for (int s = 0; s < nSpk; s++)
                    result[b][f][s] = scores[b][f][s];
            }
            for (int f = nFrames; f < nFrames + silFramesPerSpk; f++)
            {
                result[b][f] = new float[nSpk];
                for (int s = 0; s < nSpk; s++)
                    result[b][f][s] = float.PositiveInfinity;
            }
        }
        return result;
    }

    private (int[][] topkIndices, bool[][] isDisabled) GetTopkIndices(
        float[][][] scores, int spkcacheLen, int silFramesPerSpk, int maxIndex)
    {
        int batchSize = scores.Length;
        int nFrames = scores[0].Length;
        int nFramesNoSil = nFrames - silFramesPerSpk;
        int nSpk = _cfg.NSpk;

        var topkIndicesSorted = new int[batchSize][];
        var isDisabled = new bool[batchSize][];

        for (int b = 0; b < batchSize; b++)
        {
            // Flatten scores per (speaker, frame) to single list.
            var flat = new List<(int s, int f, float score)>();
            for (int s = 0; s < nSpk; s++)
                for (int f = 0; f < nFrames; f++)
                    flat.Add((s, f, scores[b][f][s]));

            // Take top spkcacheLen largest scores.
            var topk = flat
                .OrderByDescending(x => x.score)
                .Take(spkcacheLen)
                .Select(x => (x.s, x.f, x.score, index: x.s * nFrames + x.f))
                .ToList();

            var indices = topk.Select(x => x.score != float.NegativeInfinity ? x.index : maxIndex).ToArray();
            var sorted = indices.OrderBy(x => x).ToArray();
            isDisabled[b] = new bool[spkcacheLen];
            for (int i = 0; i < spkcacheLen; i++)
            {
                isDisabled[b][i] = sorted[i] == maxIndex || sorted[i] >= nFramesNoSil;
                if (isDisabled[b][i])
                    sorted[i] = 0;
                else
                    sorted[i] %= nFrames;
            }
            topkIndicesSorted[b] = sorted;
        }

        return (topkIndicesSorted, isDisabled);
    }

    private (float[][][] emb, float[][][] preds) GatherSpkcacheAndPreds(
        float[][][] embSeq,
        float[][][] preds,
        int[][] topkIndices,
        bool[][] isDisabled,
        float[][] meanSilEmb)
    {
        int batchSize = embSeq.Length;
        int spkcacheLen = topkIndices[0].Length;
        int embDim = embSeq[0][0].Length;
        int nSpk = _cfg.NSpk;

        var embGathered = new float[batchSize][][];
        var predsGathered = new float[batchSize][][];

        for (int b = 0; b < batchSize; b++)
        {
            embGathered[b] = new float[spkcacheLen][];
            predsGathered[b] = new float[spkcacheLen][];
            for (int i = 0; i < spkcacheLen; i++)
            {
                if (isDisabled[b][i])
                {
                    embGathered[b][i] = meanSilEmb[b] != null
                        ? (float[])meanSilEmb[b].Clone()
                        : new float[embDim];
                    predsGathered[b][i] = new float[nSpk];
                }
                else
                {
                    int f = topkIndices[b][i];
                    embGathered[b][i] = (float[])embSeq[b][f].Clone();
                    predsGathered[b][i] = (float[])preds[b][f].Clone();
                }
            }
        }

        return (embGathered, predsGathered);
    }

    #endregion

    #region Tensor utilities

    private static float[][][] SliceFrames(float[][][] tensor, int start, int end)
    {
        int batchSize = tensor.Length;
        var result = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            int len = Math.Max(0, end - start);
            result[b] = new float[len][];
            for (int f = 0; f < len; f++)
                result[b][f] = tensor[b][start + f];
        }
        return result;
    }

    private static float[][][] ConcatenateFrames(float[][][] left, float[][][] right)
    {
        int batchSize = left.Length;
        var result = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            int len = left[b].Length + right[b].Length;
            result[b] = new float[len][];
            for (int f = 0; f < left[b].Length; f++)
                result[b][f] = left[b][f];
            for (int f = 0; f < right[b].Length; f++)
                result[b][left[b].Length + f] = right[b][f];
        }
        return result;
    }

    private static float[][][] CreateEmptyFrames(int batchSize, int frames, int dim)
    {
        var result = new float[batchSize][][];
        for (int b = 0; b < batchSize; b++)
        {
            result[b] = new float[frames][];
            for (int f = 0; f < frames; f++)
                result[b][f] = new float[dim];
        }
        return result;
    }

    private static float Sum(float[] values)
    {
        float sum = 0f;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        return sum;
    }

    private static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;

    #endregion
}
