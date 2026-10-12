namespace VoiceType.Uno.Services.Playback;

/// <summary>
/// Streaming pitch-preserving time stretch (WSOLA: waveform-similarity overlap-add) for
/// mono speech playback at 0.5–2×.
/// <para>
/// Output is built from 40 ms Hann-windowed frames of the input placed every 20 ms
/// (synthesis hop). Frame k is taken near input position <c>k · 20 ms · speed</c>, shifted
/// by up to ±12 ms to the spot that best continues the previous frame's waveform, so
/// overlapping frames add in phase (no comb/echo, no clicks). At 1× the frames are taken
/// back to back and the overlap-add reconstructs the input exactly.
/// </para>
/// <para>
/// Input is pulled from a callback; memory is a few frames. Not thread-safe; the caller
/// serializes <see cref="Read"/>, <see cref="Reset"/> and <see cref="Speed"/>.
/// </para>
/// </summary>
public sealed class WsolaTimeStretcher
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4.0;

    private const int MarkCapacity = 4096; // ~80 s of hops

    private readonly Func<Span<float>, int> _read;
    private readonly int _hop;      // synthesis hop Hs
    private readonly int _window;   // frame length W = 2·Hs
    private readonly int _tolerance; // search ±Δ
    private readonly float[] _hann;
    private readonly float[] _overlap; // output accumulator, length W
    private readonly float[] _ready;   // finished output of the current hop
    private readonly float[] _region;  // search scratch
    private readonly float[] _template;
    private readonly float[] _frame;

    private float[] _in = new float[16384];
    private long _inStart;   // absolute input index of _in[0]
    private int _inCount;
    private long _inputEnd = long.MaxValue; // absolute index where the input ended (known after EOF)

    private double _anaPos;  // nominal input position of the next frame
    private long _prevPos;   // input position of the previous frame
    private int _readyOffset, _readyCount;
    private bool _finished;
    private double _speed = 1.0;

    private readonly long[] _markOut = new long[MarkCapacity];
    private readonly double[] _markIn = new double[MarkCapacity];
    private readonly double[] _markSpeed = new double[MarkCapacity];
    private int _markHead, _markCount;

    /// <param name="sampleRate">Input/output rate (frame sizes scale with it).</param>
    /// <param name="read">Fills the span with the next input samples; returns the count, 0 at the end.</param>
    public WsolaTimeStretcher(int sampleRate, Func<Span<float>, int> read)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        _read = read ?? throw new ArgumentNullException(nameof(read));
        SampleRate = sampleRate;
        _hop = sampleRate / 50;                       // 20 ms
        _window = 2 * _hop;                           // 40 ms
        _tolerance = Math.Max(1, sampleRate * 12 / 1000); // ±12 ms ≥ one pitch period down to ~85 Hz
        _hann = new float[_window];
        for (int i = 0; i < _window; i++)
            _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _window)); // periodic: w[i] + w[i+Hs] = 1
        _overlap = new float[_window];
        _ready = new float[_hop];
        _region = new float[2 * _tolerance + _hop];
        _template = new float[_hop];
        _frame = new float[_window];
        Reset(0);
    }

    public int SampleRate { get; }

    /// <summary>Playback speed (input seconds per output second), clamped to [<see cref="MinSpeed"/>, <see cref="MaxSpeed"/>]. Takes effect at the next 20 ms hop.</summary>
    public double Speed
    {
        get => _speed;
        set => _speed = double.IsFinite(value) ? Math.Clamp(value, MinSpeed, MaxSpeed) : 1.0;
    }

    /// <summary>Output samples produced since <see cref="Reset"/>.</summary>
    public long OutputPosition { get; private set; }

    /// <summary>True when the input has ended and every output sample was read.</summary>
    public bool IsFinished => _finished && _readyCount == 0;

    /// <summary>
    /// Restarts at <paramref name="inputPosition"/>: the caller has positioned the input so the
    /// next <c>read</c> returns that sample. Output position 0 then maps to it.
    /// </summary>
    public void Reset(long inputPosition)
    {
        _inStart = inputPosition - _hop;
        _inCount = _hop;
        Array.Clear(_in, 0, _hop); // history before the start is unknown: only the faded-out half of frame −1 sees it
        _inputEnd = long.MaxValue;
        Array.Clear(_overlap);
        _readyOffset = _readyCount = 0;
        _finished = false;
        OutputPosition = 0;
        _markHead = _markCount = 0;

        // Frame −1 sits one hop before the start; its second half overlaps the first output hop.
        // Its first half (built from the unknown history) is discarded.
        _prevPos = inputPosition - _hop;
        AddFrame(_prevPos);
        ShiftOverlap();
        _anaPos = inputPosition;
    }

    /// <summary>Fills <paramref name="output"/>; returns the count written (less only at the end).</summary>
    public int Read(Span<float> output)
    {
        int written = 0;
        while (written < output.Length)
        {
            if (_readyCount > 0)
            {
                int n = Math.Min(_readyCount, output.Length - written);
                _ready.AsSpan(_readyOffset, n).CopyTo(output[written..]);
                _readyOffset += n;
                _readyCount -= n;
                written += n;
                OutputPosition += n;
                continue;
            }
            if (_finished || !ProduceHop())
                break;
        }
        return written;
    }

    /// <summary>
    /// Input position (absolute samples, fractional) that output sample <paramref name="outputIndex"/>
    /// (counted from <see cref="Reset"/>) came from. Kept for the last ~80 s of output.
    /// </summary>
    public double InputPositionAt(long outputIndex)
    {
        if (_markCount == 0) return _anaPos;
        // Newest mark at or before outputIndex (marks are in output order).
        int lo = 0, hi = _markCount - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_markOut[MarkAt(mid)] <= outputIndex) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        int m = MarkAt(Math.Max(found, 0));
        return _markIn[m] + (outputIndex - _markOut[m]) * _markSpeed[m];
    }

    private int MarkAt(int i) => (_markHead - _markCount + i + MarkCapacity) % MarkCapacity;

    private bool ProduceHop()
    {
        long nominal = (long)Math.Round(_anaPos);
        Ensure(nominal + _tolerance + _window);
        if (nominal >= _inputEnd)
        {
            _finished = true;
            return false;
        }

        double speed = _speed;
        long pos;
        if (Math.Abs(speed - 1.0) < 1e-9)
        {
            pos = _prevPos + _hop; // natural continuation: exact reconstruction
            _anaPos = pos;
        }
        else
        {
            pos = BestCandidate(nominal);
        }

        // Switching back to 1× snaps to the previous frame's continuation, which may sit up to
        // Δ behind the nominal position; keep reported positions monotonic.
        double hopInput = _markCount > 0
            ? Math.Max(_anaPos, _markIn[MarkAt(_markCount - 1)] + _markSpeed[MarkAt(_markCount - 1)] * _hop)
            : _anaPos;
        AddFrame(pos);
        _overlap.AsSpan(0, _hop).CopyTo(_ready);
        ShiftOverlap();

        int count = _hop;
        if (_inputEnd != long.MaxValue)
            count = (int)Math.Clamp(Math.Ceiling((_inputEnd - hopInput) / speed), 0, _hop);
        AddMark(OutputPosition, hopInput, speed);
        _readyOffset = 0;
        _readyCount = count;

        _prevPos = pos;
        _anaPos += _hop * speed;
        Trim();
        if (count < _hop) _finished = true;
        return count > 0;
    }

    /// <summary>Input position in [nominal − Δ, nominal + Δ] whose first hop best matches the natural continuation of the previous frame.</summary>
    private long BestCandidate(long nominal)
    {
        long regionStart = Math.Max(nominal - _tolerance, _inStart);
        int candidates = (int)(nominal + _tolerance - regionStart) + 1;
        if (candidates <= 1) return regionStart;
        Copy(regionStart, _region.AsSpan(0, candidates - 1 + _hop));
        Copy(_prevPos + _hop, _template);

        // Coarse pass: every 2nd lag on every 2nd sample; then refine ±1 at full resolution.
        int best = (int)(nominal - regionStart);
        double bestScore = Score(best, 2);
        for (int d = 0; d < candidates; d += 2)
        {
            double s = Score(d, 2);
            if (s > bestScore) { bestScore = s; best = d; }
        }
        int coarse = best;
        bestScore = Score(coarse, 1);
        for (int d = Math.Max(0, coarse - 1); d <= Math.Min(candidates - 1, coarse + 1); d++)
        {
            double s = Score(d, 1);
            if (s > bestScore) { bestScore = s; best = d; }
        }
        return regionStart + best;
    }

    private double Score(int lag, int step)
    {
        double corr = 0, energy = 1e-9;
        var region = _region.AsSpan(lag, _hop);
        for (int i = 0; i < _hop; i += step)
        {
            float x = region[i];
            corr += x * _template[i];
            energy += x * x;
        }
        return corr / Math.Sqrt(energy);
    }

    private void AddFrame(long pos)
    {
        Ensure(pos + _window);
        Copy(pos, _frame);
        for (int i = 0; i < _window; i++)
            _overlap[i] += _frame[i] * _hann[i];
    }

    private void ShiftOverlap()
    {
        Array.Copy(_overlap, _hop, _overlap, 0, _window - _hop);
        Array.Clear(_overlap, _window - _hop, _hop);
    }

    private void AddMark(long output, double input, double speed)
    {
        _markOut[_markHead] = output;
        _markIn[_markHead] = input;
        _markSpeed[_markHead] = speed;
        _markHead = (_markHead + 1) % MarkCapacity;
        if (_markCount < MarkCapacity) _markCount++;
    }

    /// <summary>Copies absolute input [start, start + dest.Length) into dest; zero outside the buffered input.</summary>
    private void Copy(long start, Span<float> dest)
    {
        dest.Clear();
        long from = Math.Max(start, _inStart);
        long to = Math.Min(start + dest.Length, _inStart + _inCount);
        if (to > from)
            _in.AsSpan((int)(from - _inStart), (int)(to - from)).CopyTo(dest[(int)(from - start)..]);
    }

    /// <summary>Reads input until absolute index <paramref name="endExclusive"/> is buffered or the input ends.</summary>
    private void Ensure(long endExclusive)
    {
        while (_inputEnd == long.MaxValue && _inStart + _inCount < endExclusive)
        {
            int need = (int)Math.Min(endExclusive - (_inStart + _inCount), 1 << 20);
            if (_inCount + need > _in.Length)
                Array.Resize(ref _in, Math.Max(_in.Length * 2, _inCount + need));
            int got = _read(_in.AsSpan(_inCount, need));
            if (got <= 0)
            {
                _inputEnd = _inStart + _inCount;
                break;
            }
            _inCount += got;
        }
    }

    /// <summary>Drops input no later frame or template can reach.</summary>
    private void Trim()
    {
        long keepFrom = Math.Min((long)Math.Floor(_anaPos) - _tolerance - 1, _prevPos + _hop);
        int drop = (int)Math.Clamp(keepFrom - _inStart, 0, _inCount);
        if (drop < _in.Length / 2) return; // compact rarely
        Array.Copy(_in, drop, _in, 0, _inCount - drop);
        _inStart += drop;
        _inCount -= drop;
    }
}
