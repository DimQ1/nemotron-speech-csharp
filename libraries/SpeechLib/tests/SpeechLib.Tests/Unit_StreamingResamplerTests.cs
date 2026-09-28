using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_StreamingResamplerTests
{
    private static float[] Tone(double frequencyHz, int sampleRate, double seconds, float amplitude = 0.5f)
    {
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate);
        return samples;
    }

    private static float[] ResampleAll(StreamingResampler resampler, ReadOnlySpan<float> input, int blockSize)
    {
        var output = new List<float>();
        var scratch = new float[resampler.MaxOutputCount(blockSize)];
        for (var offset = 0; offset < input.Length; offset += blockSize)
        {
            var block = input.Slice(offset, Math.Min(blockSize, input.Length - offset));
            var produced = resampler.Process(block, scratch);
            output.AddRange(scratch.AsSpan(0, produced).ToArray());
        }
        return output.ToArray();
    }

    /// <summary>RMS amplitude of the steady-state part (skips the filter's start-up transient).</summary>
    private static double SteadyRms(float[] samples, int skip)
    {
        double sum = 0;
        var count = 0;
        for (var i = skip; i < samples.Length - skip; i++, count++)
            sum += (double)samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, count));
    }

    /// <summary>Goertzel power of one bin, normalised to amplitude.</summary>
    private static double ToneAmplitude(float[] samples, int sampleRate, double frequencyHz, int skip)
    {
        var n = samples.Length - 2 * skip;
        var w = 2 * Math.PI * frequencyHz / sampleRate;
        double re = 0, im = 0;
        for (var i = 0; i < n; i++)
        {
            var s = samples[skip + i];
            re += s * Math.Cos(w * i);
            im -= s * Math.Sin(w * i);
        }
        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(44100, 16000)]
    [InlineData(96000, 16000)]
    [InlineData(22050, 16000)]
    [InlineData(8000, 16000)]
    public void Process_ProducesExpectedSampleCountForRatio(int fromRate, int toRate)
    {
        var resampler = new StreamingResampler(fromRate, toRate);
        var input = new float[fromRate * 2]; // 2 s
        var output = ResampleAll(resampler, input, 4800);

        var expected = toRate * 2;
        // The filter delays output by half its width; everything else must be accounted for.
        Assert.InRange(output.Length, expected - resampler.LatencySamples * toRate / fromRate - 2, expected);
    }

    [Fact]
    public void Process_PassBandToneKeepsAmplitude()
    {
        var resampler = new StreamingResampler(48000, 16000);
        var output = ResampleAll(resampler, Tone(1000, 48000, 1.0), 4800);

        var amplitude = ToneAmplitude(output, 16000, 1000, skip: 400);
        Assert.InRange(amplitude, 0.5 * 0.97, 0.5 * 1.03); // within ±0.25 dB
    }

    [Fact]
    public void Process_AttenuatesContentAboveOutputNyquist()
    {
        // 12 kHz cannot exist at 16 kHz; nearest-neighbour decimation would alias it to 4 kHz.
        var resampler = new StreamingResampler(48000, 16000);
        var output = ResampleAll(resampler, Tone(12000, 48000, 1.0), 4800);

        var aliased = ToneAmplitude(output, 16000, 4000, skip: 400);
        var residual = SteadyRms(output, 400);
        Assert.True(aliased < 0.5 * 0.001, $"alias at 4 kHz: {20 * Math.Log10(aliased / 0.5):F1} dBFS");
        Assert.True(residual < 0.5 * 0.002, $"residual RMS: {20 * Math.Log10(residual / 0.5):F1} dBFS");
    }

    [Fact]
    public void Process_ChunkedOutputMatchesOneShotOutput()
    {
        var input = Tone(700, 44100, 0.5);
        var whole = ResampleAll(new StreamingResampler(44100, 16000), input, input.Length);

        var chunked = new List<float>();
        var resampler = new StreamingResampler(44100, 16000);
        var random = new Random(42);
        var offset = 0;
        while (offset < input.Length)
        {
            var size = Math.Min(random.Next(1, 700), input.Length - offset);
            var scratch = new float[resampler.MaxOutputCount(size)];
            var produced = resampler.Process(input.AsSpan(offset, size), scratch);
            chunked.AddRange(scratch.AsSpan(0, produced).ToArray());
            offset += size;
        }

        Assert.Equal(whole.Length, chunked.Count);
        for (var i = 0; i < whole.Length; i++)
            Assert.Equal(whole[i], chunked[i], 6);
    }

    [Fact]
    public void Process_SameRateIsVerbatimPassThrough()
    {
        var resampler = new StreamingResampler(16000, 16000);
        Assert.True(resampler.IsPassThrough);
        Assert.Equal(0, resampler.LatencySamples);

        var input = Tone(300, 16000, 0.1);
        var output = new float[resampler.MaxOutputCount(input.Length)];
        var produced = resampler.Process(input, output);

        Assert.Equal(input.Length, produced);
        Assert.Equal(input, output.AsSpan(0, produced).ToArray());
    }

    [Fact]
    public void Process_DoesNotDriftOverLongSessions()
    {
        // 44.1 → 16 kHz is a non-terminating ratio; integer stepping must keep exact time.
        var resampler = new StreamingResampler(44100, 16000);
        const int seconds = 120;
        var total = 0L;
        var block = new float[4410];
        var scratch = new float[resampler.MaxOutputCount(block.Length)];
        for (var i = 0; i < seconds * 10; i++)
            total += resampler.Process(block, scratch);

        var expected = (long)seconds * 16000;
        Assert.InRange(total, expected - 64, expected);
    }

    [Fact]
    public void Reset_RestartsFromSilence()
    {
        var resampler = new StreamingResampler(48000, 16000);
        var first = ResampleAll(resampler, Tone(500, 48000, 0.2), 4800);
        resampler.Reset();
        var second = ResampleAll(resampler, Tone(500, 48000, 0.2), 4800);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Process_RejectsUndersizedOutputBuffer()
    {
        var resampler = new StreamingResampler(48000, 16000);
        Assert.Throws<ArgumentException>(() => resampler.Process(new float[4800], new float[10]));
    }
}
