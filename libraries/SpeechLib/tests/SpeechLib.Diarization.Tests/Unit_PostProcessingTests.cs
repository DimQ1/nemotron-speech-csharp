namespace SpeechLib.Diarization.Tests;

public sealed class Unit_PostProcessingTests
{
    private static float[] Matrix(int frames, params (int From, int To, int Spk, float P)[] fills)
    {
        var m = new float[frames * 8];
        foreach (var (from, to, spk, p) in fills)
            for (int f = from; f < to; f++) m[f * 8 + spk] = p;
        return m;
    }

    [Fact]
    public void Process_ShouldBuildTurnsAtThreshold()
    {
        var m = Matrix(1000, (100, 300, 0, 0.9f), (400, 700, 1, 0.8f), (650, 650, 1, 0f), (200, 250, 2, 0.49f));
        var turns = TurnBuilder.Process(m, PostProcessingOptions.Raw);
        Assert.Equal(2, turns.Count);
        Assert.Equal(new SpeakerTurn(1.0, 3.0, 0), turns[0]);
        Assert.Equal(1, turns[1].Speaker);
        Assert.Equal(4.0, turns[1].Start, 6);
        Assert.Equal(7.0, turns[1].End, 6);
    }

    [Fact]
    public void Process_ShouldMergeGapsUpToTheLimitAndNoLonger()
    {
        // gap of exactly 20 frames (0.2 s) is bridged, 21 frames is not
        var m = Matrix(600, (0, 100, 0, 0.9f), (120, 200, 0, 0.9f), (221, 300, 0, 0.9f));
        var opts = new PostProcessingOptions { MinTurnSeconds = 0, MergeGapSeconds = 0.2 };
        var turns = TurnBuilder.Process(m, opts);
        Assert.Equal(2, turns.Count);
        Assert.Equal(0.0, turns[0].Start, 6);
        Assert.Equal(2.0, turns[0].End, 6);
        Assert.Equal(2.21, turns[1].Start, 6);
    }

    [Fact]
    public void Process_ShouldDropTurnsShorterThanTheMinimum()
    {
        var m = Matrix(500, (10, 30, 0, 0.9f), (100, 400, 1, 0.9f));
        var turns = TurnBuilder.Process(m, new PostProcessingOptions { MinTurnSeconds = 0.25, MergeGapSeconds = 0 });
        Assert.Single(turns);
        Assert.Equal(1, turns[0].Speaker);
    }

    [Fact]
    public void Process_ShouldMeasureOverlapPerTurn()
    {
        var m = Matrix(500, (0, 300, 0, 0.9f), (200, 400, 1, 0.9f));
        var turns = TurnBuilder.Process(m, PostProcessingOptions.Raw);
        Assert.Equal(2, turns.Count);
        Assert.Equal(1.0, turns[0].OverlapSeconds, 6);
        Assert.Equal(1.0, turns[1].OverlapSeconds, 6);
        Assert.True(turns[0].HasOverlap);
        Assert.Equal(3.0, turns[0].Duration, 6);
    }

    [Fact]
    public void Process_ShouldBeIndependentOfHowFramesAreChunked()
    {
        var m = Matrix(800, (5, 120, 0, 0.9f), (300, 640, 3, 0.7f), (330, 360, 0, 0.9f));
        var whole = TurnBuilder.Process(m, PostProcessingOptions.Default);

        var b = new TurnBuilder(PostProcessingOptions.Default);
        var closed = new List<SpeakerTurn>();
        for (int i = 0; i < m.Length; i += 8 * 7)
            b.Push(m.AsSpan(i, Math.Min(8 * 7, m.Length - i)), closed);
        b.Finish(closed);
        TurnBuilder.SortTurns(closed);
        Assert.Equal(whole, closed);
    }

    [Fact]
    public void Push_ShouldCloseATurnOnlyOnceItCanNoLongerGrow()
    {
        var b = new TurnBuilder(new PostProcessingOptions { MinTurnSeconds = 0, MergeGapSeconds = 0.1 });
        var closed = new List<SpeakerTurn>();
        b.Push(Matrix(50, (0, 50, 0, 0.9f)), closed);
        Assert.Empty(closed);
        var open = new List<SpeakerTurn>();
        b.SnapshotOpen(open);
        Assert.Single(open);
        Assert.Equal(0.5, open[0].End, 6);

        b.Push(new float[8 * 10], closed);    // 10 silent frames: still within the 0.1 s bridge
        Assert.Empty(closed);
        b.Push(new float[8], closed);         // the 11th one closes it
        Assert.Single(closed);
        Assert.Equal(0.5, closed[0].End, 6);
    }

    [Fact]
    public void Options_ShouldRejectNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnBuilder(new PostProcessingOptions { Threshold = 1.2f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnBuilder(new PostProcessingOptions { MinTurnSeconds = -1 }));
    }

    [Fact]
    public void Timeline_ShouldAppendAverageAndDiscardAcrossPages()
    {
        var t = new SpeakerActivityTimeline();
        var m = Matrix(5000, (0, 5000, 2, 0.5f), (4096, 5000, 4, 1f));
        t.Append(m.AsSpan(0, 8 * 3000));
        t.Append(m.AsSpan(8 * 3000));
        Assert.Equal(5000, t.FrameCount);
        Assert.Equal(50.0, t.FrontierSeconds, 6);
        Assert.Equal(1f, t.Get(4500, 4));
        Assert.Equal(0f, t.Get(4000, 4));
        Assert.Equal(0f, t.Get(9999, 2));

        Span<float> mean = stackalloc float[8];
        Assert.Equal(200, t.Average(4000, 4200, mean));
        Assert.Equal(0.5f, mean[2], 5);
        Assert.Equal(104f / 200, mean[4], 5);
        Assert.Equal(0, t.Average(6000, 6100, mean));

        Assert.Equal(m, t.ToMatrix());
        t.DiscardBefore(4100);
        Assert.Equal(4096, t.FirstFrame);
        Assert.Equal(0f, t.Get(10, 2));
        Assert.Equal(1f, t.Get(4500, 4));
        Assert.Equal(m.AsSpan(4096 * 8).ToArray(), t.ToMatrix());
        t.Append(new float[8 * 10]);
        Assert.Equal(5010, t.FrameCount);
    }

    [Fact]
    public void Timeline_ShouldKeepTheLastPartialPageWhenDiscarding()
    {
        var t = new SpeakerActivityTimeline();
        t.Append(new float[8 * 2500]);
        t.DiscardBefore(100_000);
        Assert.Equal(2048, t.FirstFrame);
        Assert.Equal(2500, t.FrameCount);
        t.Append(new float[8 * 100]);
        Assert.Equal(2600, t.FrameCount);
    }
}
