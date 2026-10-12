using SpeechLib.Models;

namespace SpeechLib.Diarization.Tests;

public sealed class Unit_SpeakerAssignerTests
{
    private static WordTiming W(string w, double s, double e) => new() { Word = w, StartSeconds = s, EndSeconds = e };

    /// <summary>9 s: spk0 [0,3), spk1 [3,6), silence [6,8), spk1 0.9 + spk0 0.6 overlapped [8,9).</summary>
    private static SpeakerActivityTimeline Timeline()
    {
        var m = new float[900 * 8];
        void Fill(int a, int b, int k, float p) { for (int f = a; f < b; f++) m[f * 8 + k] = p; }
        Fill(0, 300, 0, 0.9f);
        Fill(300, 600, 1, 0.9f);
        Fill(800, 900, 1, 0.9f);
        Fill(800, 900, 0, 0.6f);
        return SpeakerActivityTimeline.FromMatrix(m);
    }

    [Fact]
    public void AssignWords_ShouldPickTheTopSpeakerOfTheWindow()
    {
        var l = SpeakerAssigner.AssignWords(Timeline(), [W("a", 0.2, 0.6), W("b", 3.1, 3.5)]);
        Assert.Equal(0, l[0].Speaker);
        Assert.Equal(0.9f, l[0].Probability, 3);
        Assert.False(l[0].IsRevisable);
        Assert.False(l[0].IsInherited);
        Assert.Equal(1, l[1].Speaker);
        Assert.False(l[1].HasOverlap);
    }

    [Fact]
    public void AssignWords_ShouldAnchorTheWindowAtTheWordStart()
    {
        // The word starts 0.1 s before speaker 1 takes over but runs far into speaker 1's turn: the 0.5 s anchor still
        // sees 0.4 s of speaker 1 and 0.1 s of speaker 0, so speaker 1 wins. With a 0.2 s anchor the window would split 10:10 and neither would reach the threshold.
        var words = new[] { W("x", 2.9, 5.0) };
        Assert.Equal(1, SpeakerAssigner.AssignWords(Timeline(), words)[0].Speaker);

        // Window [5.9, 6.4) holds only 0.1 s of speaker 1 (mean 0.18): below the threshold, nothing to inherit.
        var l = SpeakerAssigner.AssignWords(Timeline(), [W("y", 5.9, 7.5)]);
        Assert.Equal(-1, l[0].Speaker);
        Assert.False(l[0].IsInherited);
    }

    [Fact]
    public void AssignWords_ShouldInheritThePreviousSpeakerWhenNothingIsAboveThreshold()
    {
        var l = SpeakerAssigner.AssignWords(Timeline(), [W("a", 3.1, 3.5), W("silent", 6.2, 6.6), W("again", 7.0, 7.4)]);
        Assert.Equal(1, l[0].Speaker);
        Assert.Equal(1, l[1].Speaker);
        Assert.True(l[1].IsInherited);
        Assert.False(l[1].IsRevisable);
        Assert.Equal(1, l[2].Speaker);
        Assert.True(l[2].IsInherited);
    }

    [Fact]
    public void AssignWords_ShouldReturnUnknownForALeadingSilentWord()
    {
        var l = SpeakerAssigner.AssignWords(Timeline(), [W("silent", 6.2, 6.6), W("a", 0.0, 0.3)]);
        Assert.Equal(-1, l[0].Speaker);
        Assert.False(l[0].IsKnown);
        Assert.Equal(0, l[1].Speaker);
    }

    [Fact]
    public void AssignWords_ShouldFlagOverlap()
    {
        var l = SpeakerAssigner.AssignWords(Timeline(), [W("both", 8.1, 8.5)]);
        Assert.Equal(1, l[0].Speaker);
        Assert.Equal(0, l[0].OtherSpeaker);
        Assert.True(l[0].HasOverlap);
        Assert.Equal(0.6f, l[0].OtherProbability, 3);
    }

    [Fact]
    public void AssignWords_ShouldGiveProvisionalRevisableLabelsBeyondTheFrontier()
    {
        // the timeline ends at 9.0 s
        var l = SpeakerAssigner.AssignWords(Timeline(), [W("old", 0.2, 0.6), W("edge", 8.8, 9.6), W("new", 9.5, 9.9), W("newer", 12.0, 12.3)]);
        Assert.False(l[0].IsRevisable);
        Assert.True(l[1].IsRevisable);          // window [8.8, 9.3) reaches past the frontier but has data
        Assert.Equal(1, l[1].Speaker);
        Assert.True(l[2].IsRevisable);          // no data at all: extrapolated from the last frames
        Assert.Equal(1, l[2].Speaker);
        Assert.True(l[3].IsRevisable);
    }

    [Fact]
    public void AssignWords_ShouldFallBackToThePreviousWordWhenTheFrontierIsSilent()
    {
        var m = new float[400 * 8];
        for (int f = 0; f < 200; f++) m[f * 8 + 2] = 0.9f;
        var t = SpeakerActivityTimeline.FromMatrix(m);   // 4 s, speaker 2 until 2 s, then silence
        var l = SpeakerAssigner.AssignWords(t, [W("a", 1.0, 1.4), W("later", 5.0, 5.3)]);
        Assert.Equal(2, l[0].Speaker);
        Assert.Equal(2, l[1].Speaker);
        Assert.True(l[1].IsInherited);
        Assert.True(l[1].IsRevisable);
    }

    [Fact]
    public void AssignWords_ShouldHandleZeroLengthTimingsAndEmptyInput()
    {
        var l = SpeakerAssigner.AssignWords(Timeline(), [W("z", 1.0, 1.0), W("rev", 4.0, 3.0)]);
        Assert.Equal(0, l[0].Speaker);
        Assert.Equal(1, l[1].Speaker);
        Assert.Empty(SpeakerAssigner.AssignWords(Timeline(), []));
    }

    [Fact]
    public void AssignWords_ShouldRespectTheThresholdOption()
    {
        var words = new[] { W("o", 8.1, 8.5) };
        var strict = new SpeakerAssignmentOptions { Threshold = 0.95f };
        Assert.Equal(-1, SpeakerAssigner.AssignWords(Timeline(), words, strict)[0].Speaker);
        var loose = new SpeakerAssignmentOptions { Threshold = 0.3f };
        Assert.Equal(1, SpeakerAssigner.AssignWords(Timeline(), words, loose)[0].Speaker);
    }

    [Fact]
    public void AssignSegments_ShouldTakeTheDurationMajorityAndFlagChanges()
    {
        var words = new[]
        {
            W("a", 0.2, 1.2), W("b", 1.3, 2.3),      // speaker 0, 2.0 s
            W("c", 3.1, 3.6),                          // speaker 1, 0.5 s
            W("d", 4.0, 4.5), W("e", 4.6, 5.0),       // speaker 1
        };
        var labels = SpeakerAssigner.AssignWords(Timeline(), words);
        var segs = SpeakerAssigner.AssignSegments(words, labels, [new(0, 3), new(2, 3), new(0, 0)]);
        Assert.Equal(0, segs[0].Speaker);
        Assert.True(segs[0].HasSpeakerChange);
        Assert.Equal(2.0, segs[0].SecondsBySpeaker[0], 3);
        Assert.Equal(1, segs[1].Speaker);
        Assert.False(segs[1].HasSpeakerChange);
        Assert.Equal(-1, segs[2].Speaker);
        Assert.False(segs[2].IsRevisable);
    }

    [Fact]
    public void AssignSegments_ShouldIgnoreAShortBlipWhenLookingForChanges()
    {
        var words = new[] { W("a", 0.1, 1.9), W("b", 3.1, 3.2) };
        var labels = SpeakerAssigner.AssignWords(Timeline(), words);
        var seg = SpeakerAssigner.AssignSegments(words, labels, [new(0, 2)])[0];
        Assert.Equal(0, seg.Speaker);
        Assert.False(seg.HasSpeakerChange);   // speaker 1 has 0.1 s
    }

    [Fact]
    public void AssignSegments_ShouldCountOverlappedWordTime()
    {
        var words = new[] { W("a", 8.0, 8.4), W("b", 8.5, 8.9) };
        var labels = SpeakerAssigner.AssignWords(Timeline(), words);
        var seg = SpeakerAssigner.AssignSegments(words, labels, [new(0, 2)])[0];
        Assert.Equal(1, seg.Speaker);
        Assert.Equal(0.8, seg.OverlapSeconds, 3);
        Assert.True(seg.HasOverlap);
    }

    [Fact]
    public void AssignSegments_ShouldFlagRevisableSegmentsAndValidateRanges()
    {
        var words = new[] { W("a", 0.2, 0.6), W("new", 9.5, 9.9) };
        var labels = SpeakerAssigner.AssignWords(Timeline(), words);
        var segs = SpeakerAssigner.AssignSegments(words, labels, [new(0, 1), new(0, 2)]);
        Assert.False(segs[0].IsRevisable);
        Assert.True(segs[1].IsRevisable);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeakerAssigner.AssignSegments(words, labels, [new(1, 5)]));
        Assert.Throws<ArgumentException>(() => SpeakerAssigner.AssignSegments(words, labels[..1], [new(0, 1)]));
    }

    [Fact]
    public void AssignSegment_ShouldWorkFromTimeOnly()
    {
        var t = Timeline();
        var a = SpeakerAssigner.AssignSegment(t, 0.0, 5.0);
        Assert.Equal(2.0, a.SecondsBySpeaker[1], 2);   // 3 s speaker 0 vs 2 s speaker 1
        Assert.Equal(0, a.Speaker);
        Assert.True(a.HasSpeakerChange);
        var overlapped = SpeakerAssigner.AssignSegment(t, 8.0, 9.0);
        Assert.Equal(1.0, overlapped.OverlapSeconds, 2);
        Assert.True(overlapped.HasOverlap);
        Assert.Equal(-1, SpeakerAssigner.AssignSegment(t, 6.2, 7.0).Speaker);
        Assert.True(SpeakerAssigner.AssignSegment(t, 8.5, 10.0).IsRevisable);
    }
}
