namespace SpeechLib.Diarization.Tests;

public sealed class Unit_AoscStateTests
{
    private const int H = AoscState.Hidden;
    private const int N = AoscState.NSpk;
    private static readonly float[] Silence = Enumerable.Repeat(-7f, H).ToArray();

    /// <summary>Embeddings whose every value is the frame id, so rows can be traced.</summary>
    private static float[] Embeds(int firstId, int count)
    {
        var e = new float[count * H];
        for (int i = 0; i < count; i++) Array.Fill(e, (float)(firstId + i), i * H, H);
        return e;
    }

    /// <summary>Frame probabilities for [cache | fifo | chunk]: speaker k active where active(k, row) is true.</summary>
    private static float[] Probs(int rows, Func<int, int> speakerOfRow)
    {
        var p = new float[rows * N];
        for (int r = 0; r < rows; r++)
        {
            int s = speakerOfRow(r);
            if (s >= 0) p[r * N + s] = 0.95f;
        }
        return p;
    }

    private static float[] Cached(AoscState s)
    {
        var c = new float[s.CachedFrames * H];
        s.CopyCached(c);
        return c;
    }

    private static float RowId(float[] rows, int i) => rows[i * H];

    [Fact]
    public void Update_ShouldOnlyFillTheFifoWhileItFits()
    {
        var s = new AoscState(fifoLen: 4, updatePeriod: 2, Silence, cacheLen: 32);
        int id = 0;
        s.Update(Embeds(id, 3), Probs(3, _ => 0));
        id += 3;
        Assert.Equal(0, s.CacheFrames);
        Assert.Equal(3, s.FifoFrames);
        Assert.Equal(new float[] { 0, 1, 2 }, Enumerable.Range(0, 3).Select(i => RowId(Cached(s), i)));
    }

    [Fact]
    public void Update_ShouldPopAtLeastTheUpdatePeriodFromTheFifoIntoTheCache()
    {
        var s = new AoscState(fifoLen: 4, updatePeriod: 2, Silence, cacheLen: 32);
        s.Update(Embeds(0, 3), Probs(3, _ => 0));
        s.Update(Embeds(3, 3), Probs(s.CachedFrames + 3, _ => 0));   // fifo would hold 6 > 4: pops max(2, 6 - 4) = 2
        Assert.Equal(2, s.CacheFrames);
        Assert.Equal(4, s.FifoFrames);
        var ids = Enumerable.Range(0, 6).Select(i => RowId(Cached(s), i)).ToArray();
        Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5 }, ids);   // cache rows first, then FIFO rows, still in order
    }

    [Fact]
    public void Update_ShouldPopMoreThanThePeriodWhenTheFifoOverflowsByMore()
    {
        var s = new AoscState(fifoLen: 4, updatePeriod: 2, Silence, cacheLen: 32);
        s.Update(Embeds(0, 9), Probs(9, _ => 0));   // 9 > 4: pops max(2, 5) = 5
        Assert.Equal(5, s.CacheFrames);
        Assert.Equal(4, s.FifoFrames);
    }

    [Fact]
    public void Update_ShouldCompressToTheCacheLengthKeepingRecentAndSilenceSlots()
    {
        const int cacheLen = 32;
        var s = new AoscState(fifoLen: 8, updatePeriod: 8, Silence, cacheLen: cacheLen);
        int id = 0;
        for (int step = 0; step < 40; step++)
        {
            // speaker 0 talks for the first 300 frames, then speaker 1
            int firstId = id;
            s.Update(Embeds(id, 4), Probs(s.CachedFrames + 4, r => id + 0 < 0 ? -1 : (firstId + r) < 300 ? 0 : 1));
            id += 4;
        }
        Assert.Equal(cacheLen, s.CacheFrames);
        Assert.True(s.FifoFrames <= 8 + 4);

        var cached = Cached(s);
        var ids = Enumerable.Range(0, s.CacheFrames).Select(i => RowId(cached, i)).ToArray();
        Assert.Contains(-7f, ids);                                         // unused speakers fill silence slots
        var real = ids.Where(v => v >= 0).ToArray();
        Assert.Equal(real.OrderBy(v => v), real);                          // kept frames stay in time order
        Assert.Equal(real.Distinct().Count(), real.Length);                // no duplicates
        Assert.True(real.Max() >= 100, "the compression must keep recent frames");
    }

    [Fact]
    public void Update_ShouldBeDeterministicIncludingTies()
    {
        float[][] run = new float[2][];
        for (int r = 0; r < 2; r++)
        {
            var s = new AoscState(fifoLen: 8, updatePeriod: 8, Silence, cacheLen: 32);
            int id = 0;
            for (int step = 0; step < 30; step++)
            {
                int first = id;
                s.Update(Embeds(id, 4), Probs(s.CachedFrames + 4, row => (first + row) % 3 == 0 ? -1 : (first + row) % 2));
                id += 4;
            }
            run[r] = Cached(s);
        }
        Assert.Equal(run[0], run[1]);
    }

    [Fact]
    public void Constructor_ShouldAcceptConfigParameters()
    {
        var p = TestConfig.Default.Aosc;
        var s = new AoscState(264, 222, TestConfig.Default.SilenceEmbed, p);
        Assert.Equal(0, s.CachedFrames);
        s.Update(Embeds(0, 10), Probs(10, _ => 1));
        Assert.Equal(10, s.FifoFrames);
    }
}
