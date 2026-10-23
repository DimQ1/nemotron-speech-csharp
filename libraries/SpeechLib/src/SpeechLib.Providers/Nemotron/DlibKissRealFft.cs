using System.Runtime.CompilerServices;

namespace SpeechLib.Nemotron;

/// <summary>
/// Single-precision real FFT that reproduces, operation by operation, the one the GenAI mel front-end uses:
/// <c>dlib::fftr</c> of dlib 19.24.7 (a templated KISS FFT: the real input is run as a complex FFT of half the size,
/// mixed radix 4 and 2, then split with the "super twiddles"), as called by ONNX Runtime Extensions'
/// <c>ComputeSTFTFrame</c>.
/// </summary>
/// <remarks>
/// <para>Why it exists: the token stream of the model is decided by argmax over joint logits, and in about one clip
/// in fourteen a near-tie flips on the ~1e-4 difference (in the log domain) between a float32 FFT and an exact one. A
/// double-precision FFT, or numpy's, differs from GenAI's float32 spectrum at that level; to be token-for-token
/// identical the float32 rounding of GenAI's FFT has to be reproduced, not just its maths.</para>
/// <para>Everything is float arithmetic in the order of the C++ source (separate multiply and add, no fused
/// multiply-add): <c>std::complex</c> product as <c>ac - bd</c>, <c>ad + bc</c>; twiddles from double
/// <c>cos</c>/<c>sin</c> rounded to float; plan factors by <c>kf_factor</c> (powers of 4, then 2, then odd primes -
/// only 2 and 4 are needed for the power-of-two sizes of the model and implemented).</para>
/// </remarks>
internal sealed class DlibKissRealFft
{
    private struct Cx
    {
        public float R;
        public float I;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Cx(float r, float i)
        {
            R = r;
            I = i;
        }
    }

    private readonly int _n;            // real FFT size (512)
    private readonly int _half;         // complex FFT size (256)
    private readonly int[] _factors;    // p0, m0, p1, m1, ...
    private readonly Cx[] _twiddles;    // _half entries
    private readonly Cx[] _superTwiddles;
    private readonly Cx[] _input;       // the real signal viewed as _half complex numbers
    private readonly Cx[] _work;

    public DlibKissRealFft(int size)
    {
        if (size < 4 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Only power-of-two sizes of at least 4 are supported.");

        _n = size;
        _half = size / 2;
        _input = new Cx[_half];
        _work = new Cx[_half];

        const double twoPi = 6.283185307179586476925286766559005768394338798;
        _twiddles = new Cx[_half];
        for (var i = 0; i < _half; i++)
        {
            var phase = -twoPi * i / _half;
            _twiddles[i] = new Cx((float)Math.Cos(phase), (float)Math.Sin(phase));
        }

        _superTwiddles = new Cx[_half / 2];
        for (var i = 0; i < _superTwiddles.Length; i++)
        {
            var phase = -3.141592653589793238462643383279502884197169399 * ((double)(i + 1) / _half + .5);
            _superTwiddles[i] = new Cx((float)Math.Cos(phase), (float)Math.Sin(phase));
        }

        _factors = Factor(_half);
    }

    /// <summary>Number of power-spectrum bins written by <see cref="Power"/> (<c>size / 2 + 1</c>).</summary>
    public int Bins => _half + 1;

    /// <summary>
    /// Real FFT of <paramref name="signal"/> (length <c>size</c>) and the power <c>re*re + im*im</c> of bins
    /// 0 to <c>size / 2</c>, in float, as <c>ComputeSTFTFrame</c> forms it.
    /// </summary>
    public void Power(ReadOnlySpan<float> signal, Span<float> power)
    {
        if (signal.Length != _n)
            throw new ArgumentException($"The FFT takes {_n} samples.", nameof(signal));
        if (power.Length < _half + 1)
            throw new ArgumentException("The power buffer is too small.", nameof(power));

        var input = _input;
        for (var k = 0; k < _half; k++)
            input[k] = new Cx(signal[2 * k], signal[2 * k + 1]);

        Work(_work, 0, input, 0, 1, 0);

        var tmp = _work;
        // freqdata[0] and freqdata[nfft_h]: real and imaginary part of the DC element of the half-size FFT
        power[0] = Square(tmp[0].R + tmp[0].I, 0f);
        power[_half] = Square(tmp[0].R - tmp[0].I, 0f);

        const float half = 0.5f;
        for (var k = 1; k <= _half / 2; k++)
        {
            var fpk = tmp[k];
            var fpnk = new Cx(tmp[_half - k].R, -tmp[_half - k].I);          // std::conj
            var f1k = new Cx(fpk.R + fpnk.R, fpk.I + fpnk.I);
            var f2k = new Cx(fpk.R - fpnk.R, fpk.I - fpnk.I);
            var tw = Mul(f2k, _superTwiddles[k - 1]);

            var upper = new Cx(f1k.R + tw.R, f1k.I + tw.I);                  // half * (f1k + tw)
            var lowerSum = new Cx(f1k.R - tw.R, f1k.I - tw.I);               // half * conj(f1k - tw)
            power[k] = Square(half * upper.R, half * upper.I);
            power[_half - k] = Square(half * lowerSum.R, half * -lowerSum.I);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Square(float re, float im) => re * re + im * im;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Cx Mul(Cx a, Cx b) => new(a.R * b.R - a.I * b.I, a.R * b.I + a.I * b.R);

    // kf_work: decimation in time, the radix of each stage from the factor list
    private void Work(Cx[] fout, int foutStart, Cx[] f, int fStart, int fstride, int factorIndex)
    {
        var p = _factors[factorIndex];
        var m = _factors[factorIndex + 1];
        var foutEnd = foutStart + p * m;
        var cursor = foutStart;
        var fCursor = fStart;

        if (m == 1)
        {
            do
            {
                fout[cursor] = f[fCursor];
                fCursor += fstride;
            }
            while (++cursor != foutEnd);
        }
        else
        {
            do
            {
                Work(fout, cursor, f, fCursor, fstride * p, factorIndex + 2);
                fCursor += fstride;
            }
            while ((cursor += m) != foutEnd);
        }

        switch (p)
        {
            case 2:
                Butterfly2(fout, foutStart, fstride, m);
                break;
            case 4:
                Butterfly4(fout, foutStart, fstride, m);
                break;
            default:
                throw new NotSupportedException($"Radix {p} is not needed for power-of-two sizes.");
        }
    }

    private void Butterfly2(Cx[] fout, int start, int fstride, int m)
    {
        for (var i = 0; i < m; i++)
        {
            var t = Mul(fout[start + m + i], _twiddles[i * fstride]);
            var a = fout[start + i];
            fout[start + m + i] = new Cx(a.R - t.R, a.I - t.I);
            fout[start + i] = new Cx(a.R + t.R, a.I + t.I);
        }
    }

    private void Butterfly4(Cx[] fout, int start, int fstride, int m)
    {
        var m2 = 2 * m;
        var m3 = 3 * m;
        for (var k = 0; k < m; k++)
        {
            var s0 = Mul(fout[start + m + k], _twiddles[k * fstride]);
            var s1 = Mul(fout[start + m2 + k], _twiddles[k * fstride * 2]);
            var s2 = Mul(fout[start + m3 + k], _twiddles[k * fstride * 3]);

            var f0 = fout[start + k];
            var s5 = new Cx(f0.R - s1.R, f0.I - s1.I);
            f0 = new Cx(f0.R + s1.R, f0.I + s1.I);                          // Fout[k] += scratch[1]
            var s3 = new Cx(s0.R + s2.R, s0.I + s2.I);
            var s4 = new Cx(s0.R - s2.R, s0.I - s2.I);
            fout[start + m2 + k] = new Cx(f0.R - s3.R, f0.I - s3.I);
            fout[start + k] = new Cx(f0.R + s3.R, f0.I + s3.I);             // Fout[k] += scratch[3]

            // forward: Fout[m] = s5 + rot_PI_2(s4), Fout[m3] = s5 - rot_PI_2(s4), rot_PI_2(z) = (imag, -real)
            fout[start + m + k] = new Cx(s5.R + s4.I, s5.I - s4.R);
            fout[start + m3 + k] = new Cx(s5.R - s4.I, s5.I + s4.R);
        }
    }

    // kf_factor: powers of 4, then 2, then odd primes; stored as p0, m0, p1, m1 ... with p[i] * m[i] = m[i-1]
    private static int[] Factor(int n)
    {
        var factors = new List<int>();
        var p = 4;
        var floorSqrt = Math.Floor(Math.Sqrt(n));
        do
        {
            while (n % p != 0)
            {
                p = p switch { 4 => 2, 2 => 3, _ => p + 2 };
                if (p > floorSqrt)
                    p = n;
            }
            n /= p;
            factors.Add(p);
            factors.Add(n);
        }
        while (n > 1);
        return factors.ToArray();
    }
}
