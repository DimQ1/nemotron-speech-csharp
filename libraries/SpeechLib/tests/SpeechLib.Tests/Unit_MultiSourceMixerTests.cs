using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_MultiSourceMixerTests
{
    private static float[] Fill(int length, float value) => Enumerable.Repeat(value, length).ToArray();

    [Fact]
    public void Mix_SingleSource_ShouldPassSamplesThrough()
    {
        var mixer = new MultiSourceMixer();
        var a = mixer.AddSource();
        mixer.Push(a, Fill(800, 0.25f));

        var output = mixer.Mix();

        Assert.Equal(800, output.Length);
        Assert.All(output, s => Assert.Equal(0.25f, s));
    }

    [Fact]
    public void Mix_UnevenPackets_ShouldNotInsertGapsOrRunFast()
    {
        var mixer = new MultiSourceMixer();
        var a = mixer.AddSource();
        var b = mixer.AddSource();
        var total = 0;

        // Both deliver 1600 samples in total, split differently per drain.
        mixer.Push(a, Fill(800, 0.1f)); mixer.Push(b, Fill(790, 0.2f));
        total += mixer.Mix().Length;
        mixer.Push(a, Fill(800, 0.1f)); mixer.Push(b, Fill(810, 0.2f));
        var second = mixer.Mix();
        total += second.Length;

        Assert.Equal(1600, total);
        Assert.All(second, s => Assert.Equal(0.3f, s, 5));
    }

    [Fact]
    public void Mix_IdleSource_ShouldStopHoldingOthersBack()
    {
        var mixer = new MultiSourceMixer(idleMixes: 2);
        var playing = mixer.AddSource();
        mixer.AddSource(); // a loopback device with nothing playing: never delivers

        var published = 0;
        for (var i = 0; i < 4; i++)
        {
            mixer.Push(playing, Fill(800, 0.5f));
            published += mixer.Mix().Length;
        }

        Assert.Equal(3200, published);
    }

    [Fact]
    public void Mix_LargeSkew_ShouldPublishBacklog()
    {
        var mixer = new MultiSourceMixer(maxSkewSamples: 1000, idleMixes: 100);
        var fast = mixer.AddSource();
        var slow = mixer.AddSource();
        mixer.Push(slow, Fill(10, 0f));

        mixer.Push(fast, Fill(1500, 0.5f));
        var output = mixer.Mix();

        Assert.Equal(1500, output.Length);
    }

    [Fact]
    public void Mix_ShouldApplyGainsAndClamp()
    {
        var mixer = new MultiSourceMixer();
        var a = mixer.AddSource(gain: 0.5f);
        var b = mixer.AddSource(gain: 1f);
        mixer.Push(a, Fill(100, 0.4f));
        mixer.Push(b, Fill(100, 0.9f));

        var output = mixer.Mix();

        Assert.All(output, s => Assert.Equal(1f, s));
        mixer.SetGain(b, 0f);
        mixer.Push(a, Fill(100, 0.4f));
        mixer.Push(b, Fill(100, 0.9f));
        Assert.All(mixer.Mix(), s => Assert.Equal(0.2f, s, 5));
    }

    [Fact]
    public void Mix_RemovedSource_ShouldBeIgnored()
    {
        var mixer = new MultiSourceMixer();
        var a = mixer.AddSource();
        var b = mixer.AddSource();
        mixer.Push(b, Fill(50, 0.9f));
        mixer.RemoveSource(b);
        mixer.Push(a, Fill(100, 0.1f));

        var output = mixer.Mix();

        Assert.Equal(100, output.Length);
        Assert.All(output, s => Assert.Equal(0.1f, s, 5));
    }
}
