using VoiceType.Uno.Services.Playback;
using Xunit;

namespace VoiceType.Uno.Tests.Playback;

/// <summary>In-memory <see cref="IPlaybackSource"/> for the DSP tests.</summary>
internal sealed class ArraySource(float[] samples, int sampleRate = 16000) : IPlaybackSource
{
    private long _position;

    public int SampleRate => sampleRate;
    public long Length => samples.Length;
    public int Reads { get; private set; }

    public void Seek(long sample) => _position = Math.Clamp(sample, 0, samples.Length);

    public int Read(Span<float> buffer)
    {
        Reads++;
        int n = (int)Math.Min(buffer.Length, samples.Length - _position);
        samples.AsSpan((int)_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }
}

internal static class Signals
{
    public static float[] Sine(double seconds, double hz, double amplitude = 0.5, int rate = 16000) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    /// <summary>Speech-like: gliding harmonic tone with a syllable-rate envelope.</summary>
    public static float[] Voice(double seconds, int rate = 16000)
    {
        var samples = new float[(int)(seconds * rate)];
        double phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / rate;
            phase += 2 * Math.PI * (140 + 40 * Math.Sin(2 * Math.PI * 0.7 * t)) / rate;
            double v = 0;
            for (int h = 1; h <= 8; h++) v += Math.Sin(h * phase) / h;
            samples[i] = (float)(0.25 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t)) * v);
        }
        return samples;
    }

    public static float[] ReadAll(Func<Span<float>, int> read, int chunk)
    {
        var all = new List<float>();
        var buffer = new float[chunk];
        int n;
        while ((n = read(buffer)) > 0) all.AddRange(buffer.AsSpan(0, n).ToArray());
        return all.ToArray();
    }

    /// <summary>Frequency from the zero-crossing rate.</summary>
    public static double Frequency(ReadOnlySpan<float> x, int rate)
    {
        int crossings = 0;
        for (int i = 1; i < x.Length; i++)
            if ((x[i - 1] < 0) != (x[i] < 0)) crossings++;
        return crossings / 2.0 * rate / x.Length;
    }

    public static double MaxStep(ReadOnlySpan<float> x)
    {
        double max = 0;
        for (int i = 1; i < x.Length; i++) max = Math.Max(max, Math.Abs(x[i] - x[i - 1]));
        return max;
    }
}

public class WsolaTimeStretcherTests
{
    private const int Rate = 16000;

    [Theory]
    [InlineData(1)]
    [InlineData(333)]
    [InlineData(4096)]
    public void SpeedOne_ShouldReproduceTheInputExactly(int chunk)
    {
        var input = Signals.Voice(2.3);
        var source = new ArraySource(input);
        var stretcher = new WsolaTimeStretcher(Rate, source.Read);

        var output = Signals.ReadAll(stretcher.Read, chunk);

        Assert.Equal(input.Length, output.Length);
        for (int i = 0; i < input.Length; i++)
            Assert.True(Math.Abs(input[i] - output[i]) < 1e-5, $"sample {i}: {input[i]} vs {output[i]}");
        Assert.True(stretcher.IsFinished);
    }

    [Fact]
    public void Reset_ShouldStartExactlyAtTheSeekPosition()
    {
        var input = Signals.Voice(2.0);
        var source = new ArraySource(input);
        var stretcher = new WsolaTimeStretcher(Rate, source.Read);
        stretcher.Read(new float[5000]);

        source.Seek(12_345);
        stretcher.Reset(12_345);
        var output = Signals.ReadAll(stretcher.Read, 512);

        Assert.Equal(input.Length - 12_345, output.Length);
        for (int i = 0; i < output.Length; i++)
            Assert.True(Math.Abs(input[12_345 + i] - output[i]) < 1e-5, $"sample {i}");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Speed_ShouldScaleDurationByOneOverSpeed(double speed)
    {
        var input = Signals.Voice(3.0);
        var stretcher = new WsolaTimeStretcher(Rate, new ArraySource(input).Read) { Speed = speed };

        var output = Signals.ReadAll(stretcher.Read, 1000);

        double expected = input.Length / speed;
        Assert.InRange(output.Length, expected - Rate / 50, expected + Rate / 50); // ±1 hop (20 ms)
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Speed_ShouldPreservePitchWithoutClicks(double speed)
    {
        var input = Signals.Sine(2.0, 220, amplitude: 0.5);
        var source = new ArraySource(input);
        var stretcher = new WsolaTimeStretcher(Rate, source.Read);
        stretcher.Speed = speed;
        source.Seek(0);
        stretcher.Reset(0);

        var output = Signals.ReadAll(stretcher.Read, 640);
        var steady = output.AsSpan(Rate / 10, output.Length - Rate / 5); // skip the edges

        Assert.InRange(Signals.Frequency(steady, Rate), 220 * 0.98, 220 * 1.02);
        // A splice out of phase shows up as a step far larger than the sine's own slope.
        Assert.True(Signals.MaxStep(steady) <= Signals.MaxStep(input) * 1.25,
            $"max step {Signals.MaxStep(steady):F4} vs input {Signals.MaxStep(input):F4}");
        // Overlap-add of aligned frames keeps the level (no dips at frame boundaries).
        for (int i = 0; i + 320 <= steady.Length; i += 320)
        {
            double peak = 0;
            foreach (var v in steady.Slice(i, 320)) peak = Math.Max(peak, Math.Abs(v));
            Assert.InRange(peak, 0.45, 0.55);
        }
    }

    [Fact]
    public void SpeedChange_ShouldBeSeamlessAndKeepPositionsMonotonic()
    {
        var input = Signals.Sine(3.0, 180);
        var stretcher = new WsolaTimeStretcher(Rate, new ArraySource(input).Read);
        var output = new List<float>();
        var buffer = new float[800];
        var speeds = new[] { 1.0, 1.5, 2.0, 0.75, 1.0, 1.25 };
        int step = 0, n;
        while ((n = stretcher.Read(buffer)) > 0)
        {
            output.AddRange(buffer.AsSpan(0, n).ToArray());
            stretcher.Speed = speeds[++step % speeds.Length];
        }

        Assert.True(Signals.MaxStep(output.ToArray()) <= Signals.MaxStep(input) * 1.25);
        double previous = -1;
        for (long o = 0; o < output.Count; o += 97)
        {
            double p = stretcher.InputPositionAt(o);
            Assert.True(p >= previous, $"position went back at output {o}");
            previous = p;
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(0.75)]
    public void InputPositionAt_ShouldTrackSpeedFromTheResetPoint(double speed)
    {
        var input = Signals.Voice(4.0);
        var source = new ArraySource(input);
        var stretcher = new WsolaTimeStretcher(Rate, source.Read) { Speed = speed };
        source.Seek(8000);
        stretcher.Reset(8000);
        stretcher.Read(new float[20_000]);

        foreach (long o in new long[] { 0, 1, 5000, 19_999 })
            Assert.InRange(stretcher.InputPositionAt(o), 8000 + o * speed - 2, 8000 + o * speed + 2);
    }
}
