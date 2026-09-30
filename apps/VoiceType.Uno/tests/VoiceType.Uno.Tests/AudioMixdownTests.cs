using VoiceType.Uno.Services.Audio;
using Xunit;

namespace VoiceType.Uno.Tests;

public class AudioMixdownTests
{
    [Fact]
    public void Average_ShouldReturnNull_WhenNeitherSideProducedSamples()
    {
        Assert.Null(AudioMixdown.Average(null, null));
        Assert.Null(AudioMixdown.Average([], []));
    }

    [Fact]
    public void Average_ShouldHalveASingleSidedBatch()
    {
        var mixed = AudioMixdown.Average([1f, -1f, 0.5f], null);

        Assert.Equal(new float[] { 0.5f, -0.5f, 0.25f }, mixed);
    }

    [Fact]
    public void Average_ShouldAverageBothSides()
    {
        var mixed = AudioMixdown.Average([1f, 0f], [0f, 0.8f]);

        Assert.Equal(new float[] { 0.5f, 0.4f }, mixed);
    }

    [Fact]
    public void Average_ShouldClampTheSum()
    {
        var mixed = AudioMixdown.Average([1f, -1f], [1f, -1f]);

        Assert.Equal(new float[] { 1f, -1f }, mixed);
    }

    [Fact]
    public void Average_ShouldPadTheShorterSideWithSilence()
    {
        var mixed = AudioMixdown.Average([1f, 1f, 1f], [1f, 1f]);

        Assert.Equal(3, mixed!.Length);
        Assert.Equal(new float[] { 1f, 1f, 0.5f }, mixed);
    }
}
