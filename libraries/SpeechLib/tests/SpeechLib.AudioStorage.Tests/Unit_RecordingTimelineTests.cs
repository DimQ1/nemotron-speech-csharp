namespace SpeechLib.AudioStorage.Tests;

public class Unit_RecordingTimelineTests
{
    [Theory]
    [InlineData("opus")]
    [InlineData("wav")]
    public async Task AddGap_ShouldRecordMutesWithoutWritingSamples(string spec)
    {
        using var dir = new TempDir();
        var format = RecordingAudioFormat.Parse(spec);
        var writer = RecordingAudioWriterFactory.Create(dir.File("a"), format, queued: false);

        writer.Append(new float[16000]);     // 0..1 s
        writer.AddGap(8000);                 // muted 0.5 s
        writer.AddGap(8000);                 // still muted: merged into one gap
        writer.Append(new float[8000]);      // 1..1.5 s
        writer.AddGap(32000);                // muted 2 s
        writer.Append(new float[1600]);
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        Assert.Equal(25600, result.Samples);
        Assert.Equal([new RecordingGap(16000, 16000), new RecordingGap(24000, 32000)], result.Gaps);
        Assert.Equal(1.0, result.Gaps[0].AtSeconds(16000));
        Assert.Equal(2.0, result.Gaps[1].DurationSeconds(16000));
        Assert.Equal(25600, RecordingAudioReader.ReadMono(result.Path).Length);
    }

    [Fact]
    public void CaptureSecondsAt_ShouldAddMutesBeforeThePosition()
    {
        var timeline = new RecordingTimeline(16000);
        timeline.Advance(16000);
        timeline.AddGap(16000);
        timeline.Advance(16000);

        Assert.Equal(0.5, timeline.CaptureSecondsAt(8000));
        Assert.Equal(3.0, timeline.CaptureSecondsAt(32000)); // 2 s of file + 1 s muted
        Assert.Equal(2.0, timeline.Seconds);
        Assert.Equal(16000, timeline.MutedSamples);
        Assert.Equal(24000, timeline.ToSample(1.5));
    }

    [Fact]
    public void AddGap_ShouldIgnoreNonPositive()
    {
        var timeline = new RecordingTimeline(16000);
        timeline.AddGap(0);
        timeline.AddGap(-5);
        Assert.Empty(timeline.Gaps);
    }
}
