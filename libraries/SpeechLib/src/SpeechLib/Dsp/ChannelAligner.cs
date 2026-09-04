namespace SpeechLib.Audio;

/// <summary>
/// Time-aligns the loopback and microphone streams for Mix mode.
/// WASAPI capture clocks are not synchronized: each drain cycle returns a
/// different number of samples per channel, and over a long session the
/// streams drift apart. Mixing or echo-cancelling by raw array index then
/// breaks down (the NLMS filter converges for one offset, then diverges).
///
/// The aligner keeps small carry-over buffers per channel and always emits
/// the SAME number of samples for both channels (the minimum available).
/// Excess samples stay buffered; when the skew exceeds <see cref="MaxCarry"/>
/// (256 ms at 16 kHz), the oldest samples of the runaway channel are dropped
/// to re-sync. A channel that delivered no samples in a cycle is reported as
/// inactive and passes the other channel through with zeros on its side, so
/// silence on one endpoint never blocks the other.
/// </summary>
public sealed class ChannelAligner
{
    /// <summary>Maximum buffered skew per channel (samples). 4096 = 256 ms at 16 kHz.</summary>
    public const int MaxCarry = 4096;

    private readonly float[] _loopCarry = new float[MaxCarry * 2];
    private readonly float[] _micCarry = new float[MaxCarry * 2];
    private int _loopLen;
    private int _micLen;
    private float[] _loopOut = new float[MaxCarry * 2];
    private float[] _micOut = new float[MaxCarry * 2];
    private float[] _zeros = new float[MaxCarry * 2];

    /// <summary>
    /// Appends the freshly drained samples of both channels and emits an
    /// aligned window of equal length for both.
    /// </summary>
    /// <returns>Aligned sample count (0 when nothing to emit). The returned
    /// arrays are internal buffers valid until the next call.</returns>
    public int Align(
        ReadOnlySpan<float> loop,
        ReadOnlySpan<float> mic,
        out float[] loopOut,
        out float[] micOut,
        out bool loopActive,
        out bool micActive)
    {
        loopActive = _loopLen > 0 || loop.Length > 0;
        micActive = _micLen > 0 || mic.Length > 0;

        if (!loopActive && !micActive)
        {
            loopOut = _loopOut;
            micOut = _micOut;
            return 0;
        }

        // One channel silent this cycle: pass the other through, zero-filled
        // on the missing side. No carry is accumulated for the live channel —
        // its samples are emitted immediately to keep latency at zero.
        if (!loopActive)
        {
            EnsureCapacity(ref _micOut, mic.Length);
            EnsureCapacity(ref _zeros, mic.Length);
            mic.CopyTo(_micOut);
            _zeros.AsSpan(0, mic.Length).Clear();
            loopOut = _zeros;
            micOut = _micOut;
            return mic.Length;
        }

        if (!micActive)
        {
            EnsureCapacity(ref _loopOut, loop.Length);
            EnsureCapacity(ref _zeros, loop.Length);
            loop.CopyTo(_loopOut);
            _zeros.AsSpan(0, loop.Length).Clear();
            loopOut = _loopOut;
            micOut = _zeros;
            return loop.Length;
        }

        // Both channels live: append to carry and emit the common length.
        AppendCarry(_loopCarry, ref _loopLen, loop);
        AppendCarry(_micCarry, ref _micLen, mic);

        var count = Math.Min(_loopLen, _micLen);
        EnsureCapacity(ref _loopOut, count);
        EnsureCapacity(ref _micOut, count);
        _loopCarry.AsSpan(0, count).CopyTo(_loopOut);
        _micCarry.AsSpan(0, count).CopyTo(_micOut);

        ConsumeCarry(_loopCarry, ref _loopLen, count);
        ConsumeCarry(_micCarry, ref _micLen, count);

        loopOut = _loopOut;
        micOut = _micOut;
        return count;
    }

    /// <summary>Discards all buffered state (e.g. on capture restart).</summary>
    public void Reset()
    {
        _loopLen = 0;
        _micLen = 0;
    }

    private static void AppendCarry(float[] carry, ref int len, ReadOnlySpan<float> data)
    {
        if (data.Length == 0)
            return;

        if (len + data.Length > carry.Length)
        {
            // Runaway channel: drop the oldest samples to re-sync, keeping
            // the newest MaxCarry samples (bounded skew of 256 ms).
            var keep = Math.Min(MaxCarry, data.Length);
            data.Slice(data.Length - keep).CopyTo(carry);
            len = keep;
            return;
        }

        data.CopyTo(carry.AsSpan(len));
        len += data.Length;
    }

    private static void ConsumeCarry(float[] carry, ref int len, int count)
    {
        if (count <= 0)
            return;

        var remaining = len - count;
        if (remaining > 0)
            Array.Copy(carry, count, carry, 0, remaining);
        len = remaining;
    }

    private static void EnsureCapacity(ref float[] buffer, int required)
    {
        if (buffer.Length < required)
            buffer = new float[Math.Max(required, buffer.Length * 2)];
    }
}
