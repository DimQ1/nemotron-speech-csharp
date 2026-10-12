using VoiceType.Uno.Services.Playback;
using Xunit;

namespace VoiceType.Uno.Tests.Playback;

public class PlaybackRendererTests
{
    private const int Rate = 16000;

    [Fact]
    public void Render_ShouldPadWithSilenceAfterTheEnd()
    {
        var input = Signals.Voice(0.5);
        var renderer = new PlaybackRenderer(new ArraySource(input));
        var buffer = new float[Rate]; // 1 s, more than the recording

        int real = renderer.Render(buffer);

        Assert.Equal(input.Length, real);
        Assert.All(buffer.AsSpan(real).ToArray(), v => Assert.Equal(0f, v));
        Assert.True(renderer.SourceEnded);
        Assert.Equal(input.Length, renderer.RenderedFrames);
    }

    [Fact]
    public void SourcePositionAt_ShouldMapPlayedFramesToTheRecording()
    {
        var renderer = new PlaybackRenderer(new ArraySource(Signals.Voice(5.0)));
        renderer.Restart(Rate); // 1 s
        renderer.Speed = 2.0;
        renderer.Render(new float[Rate]);

        Assert.Equal(Rate, renderer.SourcePositionAt(0));
        Assert.InRange(renderer.SourcePositionAt(Rate / 2), 2 * Rate - 2, 2 * Rate + 2); // 0.5 s played at 2× → 2 s
    }

    [Fact]
    public void SourcePositionAt_ShouldReachTheLengthWhenEverythingWasPlayed()
    {
        var input = Signals.Voice(0.7);
        var renderer = new PlaybackRenderer(new ArraySource(input));
        renderer.Render(new float[Rate]);

        Assert.Equal(input.Length, renderer.SourcePositionAt(Rate));
    }

    [Fact]
    public void Restart_ShouldClampAndResetTheCounters()
    {
        var input = Signals.Voice(1.0);
        var renderer = new PlaybackRenderer(new ArraySource(input));
        renderer.Render(new float[4000]);

        renderer.Restart(long.MaxValue);
        Assert.Equal(0, renderer.Render(new float[100]));
        Assert.True(renderer.SourceEnded);

        renderer.Restart(-10);
        Assert.Equal(0, renderer.SourcePositionAt(0));
        Assert.Equal(100, renderer.Render(new float[100]));
        Assert.False(renderer.SourceEnded);
    }

    [Fact]
    public void Speed_ShouldBeClampedToThePlayerRange()
    {
        var renderer = new PlaybackRenderer(new ArraySource(Signals.Voice(0.2)));

        renderer.Speed = 5;
        Assert.Equal(PlaybackCursor.MaxSpeed, renderer.Speed);
        renderer.Speed = 0.1;
        Assert.Equal(PlaybackCursor.MinSpeed, renderer.Speed);
    }
}
