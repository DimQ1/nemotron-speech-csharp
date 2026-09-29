using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_AutomaticGainControlTests
{
    private static float[] Sine(float amplitude, double seconds, int sampleRate = 16000) =>
        Enumerable.Range(0, (int)(seconds * sampleRate))
            .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / sampleRate)))
            .ToArray();

    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    [Fact]
    public void Process_QuietSpeech_ShouldBoostTowardTarget()
    {
        var agc = new AutomaticGainControl();
        var output = agc.Process(Sine(0.005f, 2.0));

        var tail = Rms(output.AsSpan(output.Length - 1600));
        Assert.InRange(tail, AutomaticGainControl.TargetRms * 0.8f, AutomaticGainControl.TargetRms * 1.2f);
    }

    [Fact]
    public void Process_LoudSpeech_ShouldPassUnchanged()
    {
        var agc = new AutomaticGainControl();
        var input = Sine(0.3f, 1.0);
        var output = agc.Process(input);

        Assert.Equal(1f, agc.Gain);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Process_ShouldLimitGain()
    {
        var agc = new AutomaticGainControl();
        agc.Process(Sine(0.0015f, 3.0));

        Assert.True(agc.Gain <= AutomaticGainControl.MaxGain);
    }

    [Fact]
    public void Process_LoudBurstAfterQuietSpeech_ShouldNotClip()
    {
        var agc = new AutomaticGainControl();
        agc.Process(Sine(0.005f, 2.0));
        var burst = agc.Process(Sine(0.5f, 0.5));

        // After the first 10 ms frame the gain has dropped below the clipping point.
        Assert.True(burst.Skip(160).All(s => Math.Abs(s) <= 0.9f + 1e-4f));
    }

    [Fact]
    public void Process_NoiseBelowGate_ShouldKeepUnityGain()
    {
        var agc = new AutomaticGainControl();
        agc.Process(Sine(AutomaticGainControl.NoiseGateRms * 0.5f, 2.0));

        Assert.Equal(1f, agc.Gain);
    }

    [Fact]
    public void Process_ShouldNotModifyInput()
    {
        var agc = new AutomaticGainControl();
        var input = Sine(0.005f, 0.5);
        var copy = input.ToArray();
        agc.Process(input);

        Assert.Equal(copy, input);
    }
}
