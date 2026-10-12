namespace SpeechLib.Diarization;

/// <summary>
/// Arrival-Order Speaker Cache + FIFO for Nemotron-3-Diarization, batch = 1.
/// Line-by-line port of transformers <c>Nemotron3DiarizationSpeakerCache</c> (update / _compress),
/// identical to <c>AoscState</c> in tools/converters/NemotronDiarization/diar_common.py.
/// Rows are 512-float embeddings and 8-float 80 ms speaker probabilities, stored row-major.
/// </summary>
internal sealed class AoscState
{
    public const int Hidden = 512;
    public const int NSpk = 8;

    private readonly int _fifoLen;
    private readonly int _updatePeriod;
    private readonly int _cacheLen;
    private readonly int _silFrames;
    private readonly float _thr;
    private readonly float _latestBoost;
    private readonly int _minPos;
    private readonly int _nStrong;
    private readonly int _nWeak;
    private readonly float[] _silence;

    private float[] _embeds = [];
    private float[] _probs = [];
    private float[] _fifo = [];
    private bool _isCompressed;

    public AoscState(int fifoLen, int updatePeriod, float[] silenceEmbed, AoscParameters p)
        : this(fifoLen, updatePeriod, silenceEmbed, p.CacheLen, p.SilenceFramesPerSpeaker, p.PredScoreThreshold,
            p.LatestFramesBoost, p.StrongBoostRate, p.WeakBoostRate, p.MinPosScoresRate)
    {
    }

    public AoscState(int fifoLen, int updatePeriod, float[] silenceEmbed,
        int cacheLen = 264, int silFramesPerSpk = 1, float predScoreThreshold = 0.25f,
        float latestFramesBoost = 0.05f, float strongBoostRate = 0.75f, float weakBoostRate = 1.5f,
        float minPosScoresRate = 0.5f)
    {
        _fifoLen = fifoLen;
        _updatePeriod = updatePeriod;
        _cacheLen = cacheLen;
        _silFrames = silFramesPerSpk;
        _thr = predScoreThreshold;
        _latestBoost = latestFramesBoost;
        _silence = silenceEmbed;
        int budget = cacheLen / NSpk - silFramesPerSpk;
        _minPos = (int)Math.Floor(budget * minPosScoresRate);
        _nStrong = (int)Math.Floor(budget * strongBoostRate);
        _nWeak = (int)Math.Floor(budget * weakBoostRate);
    }

    public int CacheFrames => _embeds.Length / Hidden;
    public int FifoFrames => _fifo.Length / Hidden;
    public int CachedFrames => CacheFrames + FifoFrames;

    /// <summary>Speaker-cache frames followed by FIFO frames: the <c>cache_embeds</c> graph input.</summary>
    public void CopyCached(Span<float> dst)
    {
        _embeds.CopyTo(dst);
        _fifo.CopyTo(dst[_embeds.Length..]);
    }

    /// <param name="chunkEmbeds">First n_chunk rows of the step's <c>chunk_embeds</c> output (no look-ahead).</param>
    /// <param name="frameProbs">The step's <c>frame_probs</c> output, (CachedFrames + n_input) x 8.</param>
    public void Update(ReadOnlySpan<float> chunkEmbeds, ReadOnlySpan<float> frameProbs)
    {
        int nc = CacheFrames;
        float[] fifoEmbeds = Concat(_fifo, chunkEmbeds);
        int n = fifoEmbeds.Length / Hidden;
        int popped = n <= _fifoLen ? 0 : Math.Min(Math.Max(_updatePeriod, n - _fifoLen), n);
        if (popped > 0)
        {
            ReadOnlySpan<float> fifoProbs = frameProbs.Slice(nc * NSpk, n * NSpk);
            ReadOnlySpan<float> stored = _isCompressed ? _probs.AsSpan(0, nc * NSpk) : frameProbs[..(nc * NSpk)];
            float[] cacheE = Concat(_embeds, fifoEmbeds.AsSpan(0, popped * Hidden));
            float[] cacheP = Concat(stored, fifoProbs[..(popped * NSpk)]);
            fifoEmbeds = fifoEmbeds.AsSpan(popped * Hidden).ToArray();
            if (cacheE.Length / Hidden > _cacheLen)
            {
                (cacheE, cacheP) = Compress(cacheE, cacheP);
                _isCompressed = true;
            }
            _embeds = cacheE;
            _probs = cacheP;
        }
        _fifo = fifoEmbeds;
    }

    private (float[] Embeds, float[] Probs) Compress(float[] embeds, float[] probs)
    {
        int n = probs.Length / NSpk;
        int nScored = n + _silFrames;
        // scores laid out speaker-major: s[spk * nScored + frame]
        var s = new float[NSpk * nScored];
        float log05 = MathF.Log(0.5f);
        var lc = new float[NSpk];
        for (int f = 0; f < n; f++)
        {
            float sumLc = 0;
            for (int k = 0; k < NSpk; k++)
            {
                lc[k] = MathF.Log(MathF.Max(1f - probs[f * NSpk + k], _thr));
                sumLc += lc[k];
            }
            for (int k = 0; k < NSpk; k++)
            {
                float p = probs[f * NSpk + k];
                float lp = MathF.Log(MathF.Max(p, _thr));
                s[k * nScored + f] = p > 0.5f ? lp - lc[k] + sumLc - log05 : float.NegativeInfinity;
            }
        }
        for (int k = 0; k < NSpk; k++)
        {
            int pos = 0;
            for (int f = 0; f < n; f++) if (s[k * nScored + f] > 0) pos++;
            if (pos >= _minPos)
            {
                for (int f = 0; f < n; f++)
                {
                    float v = s[k * nScored + f];
                    // speech (finite) but not positive -> drop
                    if (!(v > 0) && !float.IsNegativeInfinity(v)) s[k * nScored + f] = float.NegativeInfinity;
                }
            }
            for (int f = _cacheLen; f < n; f++) s[k * nScored + f] += _latestBoost;
            Boost(s.AsSpan(k * nScored, n), _nStrong, -2f * log05);
            Boost(s.AsSpan(k * nScored, n), _nWeak, -log05);
            for (int f = n; f < nScored; f++) s[k * nScored + f] = float.PositiveInfinity;
        }

        // global top-k over (speaker, frame), then back to speaker-major / frame order
        int[] idx = StableDescending(s);
        int sentinel = s.Length;
        var top = new int[_cacheLen];
        for (int i = 0; i < _cacheLen; i++)
            top[i] = float.IsNegativeInfinity(s[idx[i]]) ? sentinel : idx[i];
        Array.Sort(top);

        var outE = new float[_cacheLen * Hidden];
        var outP = new float[_cacheLen * NSpk];
        for (int i = 0; i < _cacheLen; i++)
        {
            int frame = top[i] == sentinel ? n : Math.Min(top[i] % nScored, n);
            if (frame >= n)
            {
                _silence.CopyTo(outE.AsSpan(i * Hidden, Hidden)); // probs row stays 0
            }
            else
            {
                embeds.AsSpan(frame * Hidden, Hidden).CopyTo(outE.AsSpan(i * Hidden));
                probs.AsSpan(frame * NSpk, NSpk).CopyTo(outP.AsSpan(i * NSpk));
            }
        }
        return (outE, outP);
    }

    private static void Boost(Span<float> col, int k, float boost)
    {
        if (k >= col.Length)
        {
            for (int i = 0; i < col.Length; i++) col[i] += boost;
            return;
        }
        int[] idx = StableDescending(col);
        for (int i = 0; i < k; i++) col[idx[i]] += boost;
    }

    /// <summary>
    /// Indices sorted by value descending, ties broken by lower index (same as NumPy
    /// <c>argsort(-x, kind="stable")</c> in diar_common.py). torch.topk's tie order is
    /// implementation-defined, so PyTorch parity is only guaranteed away from (near-)ties.
    /// </summary>
    private static int[] StableDescending(ReadOnlySpan<float> v)
    {
        var idx = new int[v.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        float[] vals = v.ToArray();
        Array.Sort(idx, (a, b) =>
        {
            int c = vals[b].CompareTo(vals[a]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return idx;
    }

    private static float[] Concat(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var r = new float[a.Length + b.Length];
        a.CopyTo(r);
        b.CopyTo(r.AsSpan(a.Length));
        return r;
    }
}
