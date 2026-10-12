using static SpeechLib.Diarization.Tests.DerScorer;

namespace SpeechLib.Diarization.Tests;

public sealed class Unit_DerScorerTests
{
    private static Segment S(double a, double b, string spk) => new(a, b, spk);

    [Fact]
    public void Score_ShouldBeZeroForAPerfectHypothesisWithRenamedSpeakers()
    {
        var reference = new[] { S(0, 10, "A"), S(10, 20, "B"), S(20, 30, "A") };
        var hyp = new[] { S(0, 10, "x"), S(10, 20, "y"), S(20, 30, "x") };
        var r = Score(reference, hyp, collar: 0);
        Assert.Equal(0, r.Der, 6);
        Assert.Equal(30, r.RefSeconds, 1);
    }

    [Fact]
    public void Score_ShouldSplitMissFalseAlarmAndConfusion()
    {
        var reference = new[] { S(0, 10, "A"), S(10, 20, "B"), S(20, 30, "A"), S(30, 40, "B") };
        var hyp = new[]
        {
            S(0, 10, "x"),        // right
            S(10, 15, "y"),       // right for 5 s, the other 5 s are missed
            S(20, 25, "y"),       // wrong speaker for 5 s (y is mapped to B)
            S(25, 30, "x"),       // right
            S(30, 40, "y"),       // right
            S(40, 42, "x"),       // false alarm 2 s
        };
        var r = Score(reference, hyp, collar: 0);
        Assert.Equal(40, r.RefSeconds, 1);
        Assert.Equal(5.0 / 40, r.Miss, 3);
        Assert.Equal(2.0 / 40, r.FalseAlarm, 3);
        Assert.Equal(5.0 / 40, r.Confusion, 3);
        Assert.Equal(12.0 / 40, r.Der, 3);
    }

    [Fact]
    public void Score_ShouldScoreOverlapAndUseOneToOneMapping()
    {
        var reference = new[] { S(0, 10, "A"), S(5, 15, "B") };
        var hyp = new[] { S(0, 10, "x") };                  // misses B entirely (10 s of reference speech B)
        var r = Score(reference, hyp, collar: 0);
        Assert.Equal(20, r.RefSeconds, 1);
        Assert.Equal(10.0 / 20, r.Miss, 3);
        Assert.Equal(0, r.Confusion, 3);
    }

    [Fact]
    public void Score_ShouldDropFramesAroundReferenceBoundariesByHalfTheCollarOnEachSide()
    {
        var reference = new[] { S(0, 10, "A") };
        var hyp = new[] { S(0.3, 10, "x") };                // starts 0.3 s late
        Assert.Equal(0.3 / 10, Score(reference, hyp, collar: 0).Der, 3);
        Assert.Equal(0.0, Score(reference, hyp, collar: 0.6).Der, 3);   // +-0.3 s removed: the error is gone
        Assert.True(Score(reference, hyp, collar: 0.5).Der > 0);   // +-0.25 s removed: 0.05 s of the error remains
    }

    [Fact]
    public void ReadRttm_ShouldParseSpeakerLines()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "SPEAKER f 1 1.50 2.25 <NA> <NA> spk_1 <NA> <NA>\nSPEAKER f 1 5.00 1.00 <NA> <NA> spk_2 <NA> <NA>\n# comment\n");
            var segs = ReadRttm(path);
            Assert.Equal(2, segs.Count);
            Assert.Equal(new Segment(1.5, 3.75, "spk_1"), segs[0]);
            Assert.Equal(6.0, segs[1].End, 6);
        }
        finally { File.Delete(path); }
    }
}
