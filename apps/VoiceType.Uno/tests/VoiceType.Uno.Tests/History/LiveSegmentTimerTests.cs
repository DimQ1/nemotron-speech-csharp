using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class LiveSegmentTimerTests
{
    private const int Rate = 16000;
    private const int Batch = 1600; // 0.1 s

    /// <summary>Feeds <paramref name="count"/> batches with the same tail and no commit.</summary>
    private static void Feed(LiveSegmentTimer timer, int count, string? tail = null)
    {
        for (var i = 0; i < count; i++)
            timer.OnBatch(Batch, tail, null);
    }

    [Fact]
    public void Utterance_ShouldStartAtFirstPartialAndEndAtCommitMinusEou()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.8);

        Feed(timer, 10);                       // 0.0–1.0 s silence
        timer.OnBatch(Batch, "hello", null);   // 1.0–1.1 first partial
        Feed(timer, 9, "hello");               // 1.1–2.0 (unchanged tail)
        timer.OnBatch(Batch, "hello world", null); // 2.0–2.1 last change
        Feed(timer, 7, "hello world");         // → 2.8
        timer.OnBatch(Batch, "", "hello world"); // commit at 2.9

        var segment = Assert.Single(timer.Segments);
        Assert.Equal(1.0, segment.Start, 3);
        // commit end 2.9 − 0.8 = 2.1, and the last partial change ended at 2.1
        Assert.Equal(2.1, segment.End, 3);
        Assert.Equal("hello world", segment.Text);
        Assert.Equal(2, segment.Words.Count);
        Assert.Equal(1.0, segment.Words[0].Start, 3);
        Assert.Equal(2.1, segment.Words[^1].End, 3);
        Assert.Equal(WordTimingKinds.Estimated, segment.WordTiming);
    }

    [Fact]
    public void Utterance_ShouldEndAtLastActivity_WhenClosedByTheNextUtterance()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.8);

        timer.OnBatch(Batch, "first", null);       // 0.0–0.1
        Feed(timer, 4, "first");                   // → 0.5
        Feed(timer, 50, "first");                  // long pause → 5.5, tail unchanged
        // The next utterance's first token closes the previous one in the same batch.
        timer.OnBatch(Batch, "second", "first");   // 5.5–5.6
        Feed(timer, 3, "second");                  // → 5.9
        timer.OnFlush("second");

        Assert.Equal(2, timer.Segments.Count);
        Assert.Equal(0.0, timer.Segments[0].Start, 3);
        Assert.Equal(0.3, timer.Segments[0].End, 3); // last activity 0.1, raised to the 0.3 s minimum
        Assert.Equal(5.5, timer.Segments[1].Start, 3);
        Assert.Equal(5.8, timer.Segments[1].End, 3);   // 5.6 raised to the 0.3 s minimum
        Assert.Equal(1, timer.Segments[1].Index);
    }

    [Fact]
    public void Utterance_ShouldEstimateStart_WhenCommitHadNoPartial()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.8);

        Feed(timer, 39);                          // → 3.9
        timer.OnBatch(Batch, null, "one two");    // commit at 4.0, no preview seen

        var segment = Assert.Single(timer.Segments);
        Assert.Equal(3.2, segment.End, 3);        // 4.0 − 0.8
        Assert.Equal(2.3, segment.Start, 3);      // 2 words × 0.45 s before the end
    }

    [Fact]
    public void Utterance_ShouldIgnoreAPreviewThatVanished()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.8);

        timer.OnBatch(Batch, "uh", null);         // noise preview at 0.0
        Feed(timer, 9);                           // vanished → 1.0
        timer.OnBatch(Batch, "real", null);       // 1.0–1.1
        Feed(timer, 9, "real");                   // → 2.0
        timer.OnBatch(Batch, null, "real");       // commit at 2.1

        Assert.Equal(1.0, Assert.Single(timer.Segments).Start, 3);
    }

    [Fact]
    public void Segments_ShouldNeverOverlapOrRunBackwards()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 2.0);

        timer.OnBatch(Batch, "a", null);
        timer.OnBatch(Batch, null, "a");          // commit at 0.2, lag would go negative
        timer.OnBatch(Batch, "b", null);
        timer.OnBatch(Batch, null, "b");

        var segments = timer.Segments;
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.True(s.End >= s.Start));
        Assert.True(segments[1].Start >= segments[0].End);
        Assert.True(segments[1].End <= timer.Position / (double)Rate);
    }

    [Fact]
    public void OnsetLead_ShouldMoveTheStartBackButNotIntoThePreviousSegment()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.1, onsetLeadSeconds: 0.8);

        Feed(timer, 10);                          // → 1.0
        timer.OnBatch(Batch, "one", null);        // first partial 1.0
        Feed(timer, 5, "one");                    // → 1.6
        timer.OnBatch(Batch, "", "one");          // commit at 1.7, end = 1.1
        timer.OnBatch(Batch, "two", null);        // first partial 1.7, lead → 0.9 < 1.1
        timer.OnFlush("two");

        Assert.Equal(0.2, timer.Segments[0].Start, 3);
        Assert.Equal(timer.Segments[0].End, timer.Segments[1].Start, 3); // clamped to the previous end
    }

    [Fact]
    public void VoiceLevels_ShouldMoveTheStartToTheVoicedStretchOfALatePartial()
    {
        // Parakeet shows the first words after a pause late: speech from 2.0 s, first
        // partial at 5.0 s. The voiced stretch puts the start back at 2.0 s.
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.8);
        for (var i = 0; i < 20; i++) timer.OnBatch(Batch, null, null, -90);       // 0–2 s silence
        for (var i = 0; i < 30; i++) timer.OnBatch(Batch, null, null, -20);       // 2–5 s speech, no text yet
        timer.OnBatch(Batch, "late words", null, -20);                            // 5.0 first partial
        for (var i = 0; i < 10; i++) timer.OnBatch(Batch, "late words", null, -90);
        timer.OnFlush("late words");

        Assert.Equal(2.0, Assert.Single(timer.Segments).Start, 3);
    }

    [Fact]
    public void VoiceLevels_ShouldIgnoreAnEarlierVoicedStretchSeparatedByAPause()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.8);
        for (var i = 0; i < 5; i++) timer.OnBatch(Batch, null, null, -90);
        for (var i = 0; i < 3; i++) timer.OnBatch(Batch, null, null, -20);        // 0.5–0.8 blip
        for (var i = 0; i < 20; i++) timer.OnBatch(Batch, null, null, -90);       // 0.8–2.8 pause
        timer.OnBatch(Batch, null, null, -20);                                    // 2.8 speech
        timer.OnBatch(Batch, "word", null, -20);                                  // 2.9 first partial
        timer.OnFlush("word");

        Assert.Equal(2.8, Assert.Single(timer.Segments).Start, 3);
    }

    [Fact]
    public void LevelDb_ShouldMeasureRms()
    {
        Assert.Equal(-100, LiveSegmentTimer.LevelDb(new float[160]));
        Assert.Equal(-6.02, LiveSegmentTimer.LevelDb(Enumerable.Repeat(0.5f, 160).ToArray()), 2);
    }

    [Fact]
    public void Plain_ShouldCloseAtSentenceEnd()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: false);

        Feed(timer, 5);                                // → 0.5
        timer.OnBatch(Batch, null, "Hello ");          // 0.5–0.6
        timer.OnBatch(Batch, null, "there.");          // 0.6–0.7 sentence end
        timer.OnBatch(Batch, null, " Next");           // 0.7–0.8
        timer.OnFlush(" one");

        Assert.Equal(2, timer.Segments.Count);
        Assert.Equal("Hello there.", timer.Segments[0].Text);
        Assert.Equal(0.5, timer.Segments[0].Start, 3);
        Assert.Equal(0.7, timer.Segments[0].End, 3);   // never past the batch that closed it
        Assert.Equal("Next one", timer.Segments[1].Text);
    }

    [Fact]
    public void Plain_ShouldCloseAfterAPause()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: false, plainPauseSeconds: 1.0);

        timer.OnBatch(Batch, null, "no punctuation");  // 0.0–0.1
        timer.OnBatch(Batch, null, " here");           // 0.1–0.2
        Feed(timer, 10);                               // pause → 1.2

        var segment = Assert.Single(timer.Segments);
        Assert.Equal("no punctuation here", segment.Text);
        Assert.Equal(0.0, segment.Start, 3);
        Assert.Equal(0.3, segment.End, 3);
    }

    [Fact]
    public void Position_ShouldCountOnlyFedSamples()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true);
        Feed(timer, 3);
        timer.OnBatch(800, null, null);
        Assert.Equal(3 * Batch + 800, timer.Position);
    }

    [Fact]
    public void LanguageTags_ShouldBeStrippedAndDetected()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: false);
        timer.OnBatch(Batch, null, "<ru> Привет мир.");

        Assert.Equal("ru", timer.DetectedLanguage);
        Assert.Equal("Привет мир.", Assert.Single(timer.Segments).Text);
    }

    [Fact]
    public void BuildDocument_ShouldJoinSegmentTexts()
    {
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, commitLagSeconds: 0.1);
        timer.OnBatch(Batch, "one", null);
        timer.OnBatch(Batch, null, "one");
        timer.OnBatch(Batch, "two", null);
        timer.OnFlush("two");

        var doc = timer.BuildDocument();
        Assert.Equal("one two", doc.Text);
        Assert.Equal(2, doc.Segments.Count);
        Assert.Equal(2, doc.WordCount);
    }

    [Theory]
    [InlineData("<en>  hello   world ", "hello world")]
    [InlineData(null, "")]
    [InlineData("a\nb", "a b")]
    public void CleanText_ShouldStripTagsAndCollapseWhitespace(string? raw, string expected) =>
        Assert.Equal(expected, LiveSegmentTimer.CleanText(raw));
}
