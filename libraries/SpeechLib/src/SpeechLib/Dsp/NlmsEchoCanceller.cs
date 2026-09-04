namespace SpeechLib.Audio;

/// <summary>
/// Adaptive echo canceller (NLMS) for Mix mode. The loopback (far-end)
/// signal is the reference; an adaptive FIR filter estimates the
/// speaker-to-microphone echo path and subtracts the echo estimate from
/// the microphone channel.
///
/// Filter length covers 256 ms (4096 taps at 16 kHz) so it can absorb the
/// acoustic echo delay plus the residual capture offset left after channel
/// alignment. Adaptation is frozen during double-talk (near-end speech
/// louder than the far end) and when the far end is silent, which keeps
/// the filter from diverging on the user's own voice.
/// </summary>
public sealed class NlmsEchoCanceller
{
    private readonly float[] _w;   // adaptive filter taps
    private readonly float[] _x;   // far-end ring buffer (taps window)
    private readonly int _len;
    private readonly float _mu;    // NLMS step size
    private int _pos;

    public NlmsEchoCanceller(int taps = 4096, float stepSize = 0.5f)
    {
        _len = taps;
        _mu = stepSize;
        _w = new float[taps];
        _x = new float[taps];
    }

    public void Reset()
    {
        Array.Clear(_w);
        Array.Clear(_x);
        _pos = 0;
    }

    /// <summary>
    /// Cancels echo in <paramref name="mic"/> in place using the far-end
    /// reference <paramref name="far"/>. Both spans must be time-aligned
    /// (see <see cref="ChannelAligner"/>) and of equal effective length;
    /// only the common prefix is processed.
    /// </summary>
    public void ProcessInPlace(ReadOnlySpan<float> far, Span<float> mic)
    {
        int n = Math.Min(far.Length, mic.Length);
        if (n == 0)
            return;

        // Chunk-level power estimates drive the adaptation gate.
        float farPow = 0f, micPow = 0f;
        for (int i = 0; i < n; i++)
        {
            farPow += far[i] * far[i];
            micPow += mic[i] * mic[i];
        }

        // Adapt only when the far end is active AND the mic looks like
        // (mostly) echo. micPow ≈ g²·farPow for pure echo with path gain g ≤ 1,
        // so a 1.2× threshold keeps adapting up to g ≈ 1.1 while freezing on
        // double-talk (micPow ≫ farPow when the user speaks over the echo).
        bool adapt = farPow > 1e-6f * n && micPow < 1.2f * farPow;

        for (int i = 0; i < n; i++)
        {
            _x[_pos] = far[i];

            // Echo estimate y = wᵀ·x over the ring window; also accumulate
            // window power for the NLMS normalization term.
            float y = 0f, p = 1e-8f;
            int idx = _pos;
            for (int k = 0; k < _len; k++)
            {
                float xv = _x[idx];
                y += _w[k] * xv;
                p += xv * xv;
                if (--idx < 0) idx += _len;
            }

            float e = mic[i] - y;
            mic[i] = e;

            if (adapt)
            {
                float step = _mu * e / p;
                idx = _pos;
                for (int k = 0; k < _len; k++)
                {
                    _w[k] += step * _x[idx];
                    if (--idx < 0) idx += _len;
                }
            }

            _pos++;
            if (_pos >= _len) _pos = 0;
        }
    }
}

