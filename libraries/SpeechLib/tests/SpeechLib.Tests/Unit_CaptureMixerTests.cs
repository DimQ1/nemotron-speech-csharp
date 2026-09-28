using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_CaptureMixerTests
{
    [Fact]
    public void Mix_AppliesPerChannelGain()
    {
        float[] a = [0.2f, 0.2f];
        float[] b = [0.4f, -0.4f];
        var output = new float[2];

        var count = CaptureMixer.Mix(a, 0.5f, b, 1f, output);

        Assert.Equal(2, count);
        Assert.Equal(0.5f, output[0], 6);
        Assert.Equal(-0.3f, output[1], 6);
    }

    [Fact]
    public void Mix_ShorterChannelIsSilencePastItsEnd()
    {
        float[] a = [0.1f, 0.2f, 0.3f];
        float[] b = [0.5f];
        var output = new float[3];

        var count = CaptureMixer.Mix(a, 1f, b, 1f, output);

        Assert.Equal(3, count);
        Assert.Equal(0.6f, output[0], 6);
        Assert.Equal(0.2f, output[1], 6);
        Assert.Equal(0.3f, output[2], 6);
    }

    [Fact]
    public void Mix_IdleChannelPassesTheOtherThrough()
    {
        float[] mic = [0.7f, -0.7f];
        var output = new float[2];

        var count = CaptureMixer.Mix(ReadOnlySpan<float>.Empty, 1f, mic, 1f, output);

        Assert.Equal(2, count);
        Assert.Equal(mic, output);
    }

    [Fact]
    public void Mix_ClampsToUnitRange()
    {
        float[] a = [0.9f, -0.9f];
        float[] b = [0.9f, -0.9f];
        var output = new float[2];

        CaptureMixer.Mix(a, 1f, b, 1f, output);

        Assert.Equal(1f, output[0]);
        Assert.Equal(-1f, output[1]);
    }

    [Fact]
    public void Mix_BothEmptyProducesNothing()
    {
        Assert.Equal(0, CaptureMixer.Mix(ReadOnlySpan<float>.Empty, 1f, ReadOnlySpan<float>.Empty, 1f, Span<float>.Empty));
    }

    [Fact]
    public void Mix_UndersizedOutputThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            CaptureMixer.Mix(new float[4], 1f, new float[2], 1f, new float[3]));
    }
}
