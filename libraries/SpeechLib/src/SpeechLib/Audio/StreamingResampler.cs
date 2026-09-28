using System.Numerics;

namespace SpeechLib.Audio;

/// <summary>
/// Stateful, anti-aliased sample-rate converter for live capture.
/// <para>
/// Windowed-sinc (Kaiser) polyphase FIR: the low-pass cutoff sits just below the
/// lower of the two Nyquist frequencies, so content above the output band is
/// attenuated instead of folding back into the speech band (nearest-neighbour
/// decimation aliases everything from 8–24 kHz into 0–8 kHz when going 48 → 16 kHz).
/// </para>
/// <para>
/// The converter keeps filter history and the fractional read position between
/// calls, so feeding audio in arbitrary block sizes yields the same samples as one
/// big conversion — no clicks or dropped samples at drain-interval boundaries.
/// Stepping uses the exact rational ratio, so long sessions never drift.
/// </para>
/// </summary>
public sealed class StreamingResampler
{
    /// <summary>Default number of sinc zero-crossings on each side (at the lower rate).</summary>
    public const int DefaultZeroCrossings = 32;

    /// <summary>Upper bound on the phase table size for ratios with a large reduced denominator.</summary>
    private const int MaxPhases = 1024;

    /// <summary>Kaiser β ≈ 7 gives roughly 70 dB stop-band attenuation.</summary>
    private const double KaiserBeta = 7.0;

    /// <summary>The −6 dB point is placed at this fraction of the lower Nyquist frequency.</summary>
    private const double CutoffScale = 0.94;

    private readonly int _stepNumerator;   // input samples per output sample = M / L
    private readonly int _stepDenominator;
    private readonly int _phases;          // table rows = _phases + 1 (row _phases == fraction 1.0)
    private readonly int _halfTaps;        // H: kernel support is |t| <= H input samples
    private readonly int _taps;            // 2H
    private readonly float[] _table;

    private float[] _history;
    private int _historyCount;
    private int _position;   // history index of the integer part of the next output's read position
    private int _fraction;   // numerator of the fractional part, in [0, L)

    /// <param name="fromRate">Input sample rate in Hz.</param>
    /// <param name="toRate">Output sample rate in Hz.</param>
    /// <param name="zeroCrossings">
    /// Sinc zero-crossings per side at the lower rate. More = sharper transition band and
    /// more CPU; 32 keeps the pass-band flat to ~7 kHz at 16 kHz output for ~3 M MAC/s.
    /// </param>
    public StreamingResampler(int fromRate, int toRate, int zeroCrossings = DefaultZeroCrossings)
    {
        if (fromRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate));
        if (toRate <= 0) throw new ArgumentOutOfRangeException(nameof(toRate));
        if (zeroCrossings <= 0) throw new ArgumentOutOfRangeException(nameof(zeroCrossings));

        FromRate = fromRate;
        ToRate = toRate;

        var gcd = Gcd(fromRate, toRate);
        _stepNumerator = fromRate / gcd;
        _stepDenominator = toRate / gcd;
        _phases = Math.Min(_stepDenominator, MaxPhases);

        // Kernel stretch: when downsampling the sinc is widened by the ratio so its
        // cutoff lands at the OUTPUT Nyquist; when upsampling the input Nyquist is the limit.
        var ratio = (double)fromRate / toRate;
        var stretch = Math.Max(ratio, 1.0);
        _halfTaps = (int)Math.Ceiling(zeroCrossings * stretch);
        _taps = 2 * _halfTaps;
        _table = IsPassThrough ? Array.Empty<float>() : BuildTable(_phases, _halfTaps, stretch);

        _history = new float[Math.Max(4 * _taps, 4096)];
        Reset();
    }

    /// <summary>Input sample rate in Hz.</summary>
    public int FromRate { get; }

    /// <summary>Output sample rate in Hz.</summary>
    public int ToRate { get; }

    /// <summary>True when input and output rates are equal; <see cref="Process"/> then copies verbatim.</summary>
    public bool IsPassThrough => FromRate == ToRate;

    /// <summary>Group delay introduced by the filter, in input samples.</summary>
    public int LatencySamples => IsPassThrough ? 0 : _halfTaps;

    /// <summary>
    /// Upper bound on the number of output samples <see cref="Process"/> can produce for
    /// <paramref name="inputCount"/> new input samples (accounts for buffered history).
    /// </summary>
    public int MaxOutputCount(int inputCount)
    {
        if (inputCount < 0) throw new ArgumentOutOfRangeException(nameof(inputCount));
        if (IsPassThrough) return inputCount;

        long pending = _taps + inputCount;
        return checked((int)((pending * _stepDenominator + _stepNumerator - 1) / _stepNumerator + 1));
    }

    /// <summary>Upper bound on the number of samples <see cref="Flush"/> can produce.</summary>
    public int FlushOutputCount => IsPassThrough ? 0 : MaxOutputCount(_halfTaps);

    /// <summary>
    /// Push the filter's group delay through with silence so the last input samples are
    /// emitted (one-shot file conversion). Call <see cref="Reset"/> before reusing the
    /// instance for a new stream. Returns the number of output samples written.
    /// </summary>
    public int Flush(Span<float> output)
    {
        if (IsPassThrough)
            return 0;

        return Process(new float[_halfTaps], output);
    }

    /// <summary>Discard buffered history and restart at time zero.</summary>
    public void Reset()
    {
        var priming = Math.Max(0, _halfTaps - 1);
        Array.Clear(_history, 0, priming);
        _historyCount = priming;
        _position = priming;
        _fraction = 0;
    }

    /// <summary>
    /// Convert <paramref name="input"/> and write the resulting samples to <paramref name="output"/>.
    /// Returns the number of output samples written. <paramref name="output"/> must hold at least
    /// <see cref="MaxOutputCount"/> samples for the given input length.
    /// </summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (IsPassThrough)
        {
            if (output.Length < input.Length)
                throw new ArgumentException("Output buffer is too small.", nameof(output));
            input.CopyTo(output);
            return input.Length;
        }

        if (output.Length < MaxOutputCount(input.Length))
            throw new ArgumentException("Output buffer is too small; size it with MaxOutputCount.", nameof(output));

        Append(input);

        var produced = 0;
        var lastReadable = _historyCount - 1;
        while (_position + _halfTaps <= lastReadable)
        {
            var row = PhaseRow(_fraction);
            var window = _history.AsSpan(_position - _halfTaps + 1, _taps);
            output[produced++] = Dot(window, _table.AsSpan(row * _taps, _taps));

            _fraction += _stepNumerator;
            _position += _fraction / _stepDenominator;
            _fraction %= _stepDenominator;
        }

        Compact();
        return produced;
    }

    private int PhaseRow(int fraction)
    {
        if (_phases == _stepDenominator)
            return fraction;

        // Nearest table phase; row _phases (fraction 1.0) exists, so rounding up is safe.
        return (int)(((long)fraction * _phases + _stepDenominator / 2) / _stepDenominator);
    }

    private void Append(ReadOnlySpan<float> input)
    {
        var required = _historyCount + input.Length;
        if (required > _history.Length)
        {
            var grown = new float[Math.Max(required, _history.Length * 2)];
            Array.Copy(_history, grown, _historyCount);
            _history = grown;
        }

        input.CopyTo(_history.AsSpan(_historyCount));
        _historyCount += input.Length;
    }

    /// <summary>Drop history the next output window can no longer reach.</summary>
    private void Compact()
    {
        var start = Math.Clamp(_position - _halfTaps + 1, 0, _historyCount);
        if (start == 0)
            return;

        var remaining = _historyCount - start;
        Array.Copy(_history, start, _history, 0, remaining);
        _historyCount = remaining;
        _position -= start;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var width = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - width; i += width)
            acc += new Vector<float>(a.Slice(i, width)) * new Vector<float>(b.Slice(i, width));

        var sum = Vector.Sum(acc);
        for (; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// Build the polyphase table: row k holds the kernel sampled at integer offsets
    /// j ∈ [-(H-1), H] shifted by the fractional position k / phases. Each row is normalised
    /// to unit DC gain so the pass-band level does not ripple between phases.
    /// </summary>
    private static float[] BuildTable(int phases, int halfTaps, double stretch)
    {
        var taps = 2 * halfTaps;
        var table = new float[(phases + 1) * taps];
        var cutoff = CutoffScale * 0.5 / stretch; // cycles per input sample
        var i0Beta = BesselI0(KaiserBeta);

        for (var k = 0; k <= phases; k++)
        {
            var fraction = (double)k / phases;
            var row = table.AsSpan(k * taps, taps);
            double sum = 0;

            for (var m = 0; m < taps; m++)
            {
                var t = (m - (halfTaps - 1)) - fraction;
                var value = Kernel(t, halfTaps, cutoff, i0Beta);
                row[m] = (float)value;
                sum += value;
            }

            if (sum > 0)
            {
                var norm = (float)(1.0 / sum);
                for (var m = 0; m < taps; m++)
                    row[m] *= norm;
            }
        }

        return table;
    }

    private static double Kernel(double t, int halfTaps, double cutoff, double i0Beta)
    {
        var x = t / halfTaps;
        if (x <= -1.0 || x >= 1.0)
            return 0.0;

        var window = BesselI0(KaiserBeta * Math.Sqrt(1.0 - x * x)) / i0Beta;
        var arg = 2.0 * cutoff * t;
        var sinc = arg == 0.0 ? 1.0 : Math.Sin(Math.PI * arg) / (Math.PI * arg);
        return 2.0 * cutoff * sinc * window;
    }

    /// <summary>Zeroth-order modified Bessel function of the first kind (power series).</summary>
    private static double BesselI0(double x)
    {
        var y = x * x / 4.0;
        double sum = 1.0, term = 1.0;
        for (var k = 1; k < 200; k++)
        {
            term *= y / ((double)k * k);
            sum += term;
            if (term < sum * 1e-12)
                break;
        }
        return sum;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }
}
