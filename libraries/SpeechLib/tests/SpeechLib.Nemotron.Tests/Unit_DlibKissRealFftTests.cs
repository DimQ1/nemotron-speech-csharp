using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

public sealed class Unit_DlibKissRealFftTests
{
    [Theory]
    [InlineData(4)]      // half size 2: one radix-2 stage
    [InlineData(8)]      // 4: one radix-4 stage
    [InlineData(16)]     // 8: radix 4 then 2
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(512)]    // the model's FFT: 256 complex points, four radix-4 stages
    [InlineData(1024)]
    public void Power_ShouldMatchANaiveDoublePrecisionDft(int size)
    {
        var rng = new Random(size);
        var signal = new float[size];
        for (var i = 0; i < size; i++)
            signal[i] = (float)(rng.NextDouble() * 2 - 1);

        var fft = new DlibKissRealFft(size);
        var power = new float[fft.Bins];
        fft.Power(signal, power);

        Assert.Equal(size / 2 + 1, fft.Bins);
        var peak = 0.0;
        var reference = new double[size / 2 + 1];
        for (var k = 0; k <= size / 2; k++)
        {
            double re = 0, im = 0;
            for (var n = 0; n < size; n++)
            {
                var angle = -2.0 * Math.PI * k * n / size;
                re += signal[n] * Math.Cos(angle);
                im += signal[n] * Math.Sin(angle);
            }
            reference[k] = re * re + im * im;
            peak = Math.Max(peak, reference[k]);
        }

        // float32 transform: errors of a few 1e-7 relative to the loudest bin
        for (var k = 0; k <= size / 2; k++)
            Assert.True(Math.Abs(power[k] - reference[k]) <= 1e-5 * peak, $"size {size} bin {k}: {power[k]} vs {reference[k]}");
    }

    [Fact]
    public void Power_Impulse_ShouldBeFlat()
    {
        var signal = new float[512];
        signal[0] = 1f;
        var fft = new DlibKissRealFft(512);
        var power = new float[fft.Bins];

        fft.Power(signal, power);

        Assert.All(power, p => Assert.Equal(1f, p, 1e-6));
    }

    [Fact]
    public void Power_PureTone_ShouldPeakAtItsBin()
    {
        var signal = new float[512];
        for (var n = 0; n < 512; n++)
            signal[n] = MathF.Sin(2 * MathF.PI * 37 * n / 512);
        var fft = new DlibKissRealFft(512);
        var power = new float[fft.Bins];

        fft.Power(signal, power);

        var peak = Array.IndexOf(power, power.Max());
        Assert.Equal(37, peak);
        Assert.Equal(256f * 256f, power[37], 0.5);        // (N/2)^2 for a unit sine
    }

    [Fact]
    public void Power_ShouldBeDeterministicAcrossCalls()
    {
        var rng = new Random(5);
        var signal = new float[512];
        for (var i = 0; i < signal.Length; i++)
            signal[i] = (float)rng.NextDouble();
        var fft = new DlibKissRealFft(512);
        var first = new float[fft.Bins];
        var second = new float[fft.Bins];

        fft.Power(signal, first);
        fft.Power(signal.AsSpan(), second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Constructor_AndPower_ShouldRejectBadSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlibKissRealFft(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlibKissRealFft(2));
        var fft = new DlibKissRealFft(512);
        Assert.Throws<ArgumentException>(() => fft.Power(new float[511], new float[257]));
        Assert.Throws<ArgumentException>(() => fft.Power(new float[512], new float[256]));
    }
}
