namespace SpeechLib.Nemotron;

/// <summary>
/// Streaming NeMo log-mel front-end of the Nemotron model: a port of the ORT-Extensions
/// <c>NemoStreamingMelExtractor</c> that GenAI 0.16.0 drives chunk by chunk (pre-emphasis with a carried sample,
/// 256-sample left overlap, symmetric Hann window, 512-point power spectrum, Slaney mel filterbank,
/// <c>ln(x + 2^-24)</c>, no normalisation, no dither).
/// </summary>
/// <remarks>
/// <para><b>Faithful mode (default)</b> reproduces GenAI exactly, including its one deviation from NeMo: the
/// chunk is followed by 56 zeros instead of the next samples, so the last mel frame of every chunk (and the copy
/// of it in the next chunk's 9-frame cache) is distorted by up to a few nats in quiet bins. It does not change the
/// word error rate, but it changes token ids in a few percent of clips, so token-exact parity needs it.</para>
/// <para><b>Clean mode</b>: pass the next <see cref="LookaheadFor"/> raw samples as <c>lookahead</c> and the last
/// frame sees its real window, which is what an offline NeMo mel computes. The caller has to hold a chunk back until
/// that many samples of the following chunk are available: 2.5 ms of audio at 16 kHz, but with feeds of about a
/// chunk or a batch each chunk then leaves one feed later (up to one feed interval of extra latency).</para>
/// <para>Steady state allocates nothing: all work buffers are fields, frames are written straight into the caller's
/// span (the encoder input buffer). The FFT is a float32 port of dlib's KISS FFT (<see cref="DlibKissRealFft"/>), the
/// very routine GenAI's extractor runs, so the spectrum has the same float32 rounding; the filterbank sum is float, in
/// ascending bin order over the non-zero bins only (identical to the dense sum, the skipped terms are exact zeros).</para>
/// <para><b>Platform scope of "identical to GenAI".</b> The arithmetic is the same on every platform, but the Hann window
/// (<see cref="MathF.Cos(float)"/>), the final <see cref="MathF.Log(float)"/> and the filterbank construction call the
/// platform's math library. The recorded GenAI mel is therefore reproduced <b>bit for bit on Windows x64</b> (UCRT, the
/// platform of the oracle) only. Measured on Linux x64 (glibc 2.39): 6 to 12 percent of the mel floats of the three test clips differ in the last
/// bits, by at most 7e-5 (73 ulp, quiet bins), and the token ids and text of 8 INT4 c056 clips were still identical to
/// the goldens. arm64, macOS, Android and iOS have not been measured.</para>
/// </remarks>
internal sealed class NemotronStreamingMel
{
    private readonly int _numMels;
    private readonly int _fftSize;
    private readonly int _hop;
    private readonly int _win;
    private readonly int _bins;
    private readonly int _pad;          // fftSize / 2: samples of left overlap
    private readonly int _winOffset;    // (fftSize - win) / 2: also the zeros appended after the chunk
    private readonly float _preemph;
    private readonly float _logEps;

    private readonly float[] _window;
    private readonly float[] _weights;  // non-zero filterbank weights, mel after mel
    private readonly int[] _melFirstBin;
    private readonly int[] _melBinCount;
    private readonly int[] _melWeightOffset;

    private readonly DlibKissRealFft _fft;
    private readonly float[] _frame;    // windowed samples, zero padded to the FFT size
    private readonly float[] _power;

    private readonly float[] _overlap;  // last `_pad` pre-emphasised samples of the stream so far
    private float _prev;                // last raw sample of the previous chunk
    private float[] _padded = [];

    public NemotronStreamingMel(NemotronModelConfig config)
        : this(config.NumMels, config.FftSize, config.HopLength, config.WinLength, config.SampleRate, config.Preemph, config.LogEps)
    {
    }

    public NemotronStreamingMel(int numMels, int fftSize, int hopLength, int winLength, int sampleRate, float preemph, float logEps)
    {
        if (numMels <= 0 || hopLength <= 0 || winLength <= 0 || sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(numMels), "Mel front-end sizes must be positive.");
        if (fftSize < winLength || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fftSize), "The FFT size must be a power of two that holds the window.");

        _numMels = numMels;
        _fftSize = fftSize;
        _hop = hopLength;
        _win = winLength;
        _bins = fftSize / 2 + 1;
        _pad = fftSize / 2;
        _winOffset = (fftSize - winLength) / 2;
        _preemph = preemph;
        _logEps = logEps;

        _window = new float[winLength];
        for (var i = 0; i < winLength; i++)
            _window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (winLength - 1)));

        var dense = CreateMelFilterbank(numMels, fftSize, sampleRate);
        _melFirstBin = new int[numMels];
        _melBinCount = new int[numMels];
        _melWeightOffset = new int[numMels];
        var weights = new List<float>();
        for (var m = 0; m < numMels; m++)
        {
            int first = -1, last = -1;
            for (var k = 0; k < _bins; k++)
            {
                if (dense[m * _bins + k] == 0f)
                    continue;
                if (first < 0)
                    first = k;
                last = k;
            }
            _melWeightOffset[m] = weights.Count;
            if (first < 0)
                continue; // an all-zero filter: its sum is exactly 0
            _melFirstBin[m] = first;
            _melBinCount[m] = last - first + 1;
            for (var k = first; k <= last; k++)
                weights.Add(dense[m * _bins + k]);
        }
        _weights = weights.ToArray();

        _fft = new DlibKissRealFft(fftSize);
        _frame = new float[fftSize];
        _power = new float[_bins];
        _overlap = new float[_pad];
    }

    /// <summary>Mel bins per frame.</summary>
    public int NumMels => _numMels;

    /// <summary>Number of mel frames produced for a chunk of <paramref name="chunkSamples"/> samples.</summary>
    public int FramesFor(int chunkSamples)
    {
        var span = _pad + chunkSamples - _win;   // negative when not even one window fits (floor division would give 0 frames)
        return span < 0 ? 0 : span / _hop + 1;
    }

    /// <summary>
    /// Raw samples that follow a chunk of <paramref name="chunkSamples"/> samples and still fall inside the analysis
    /// window of its last frame (40 for the shipped 560 ms and 1120 ms chunks); what clean mode needs as lookahead.
    /// </summary>
    public int LookaheadFor(int chunkSamples)
    {
        var lastWindowEnd = _winOffset + _hop * (FramesFor(chunkSamples) - 1) + _win; // index into the padded signal
        return Math.Clamp(lastWindowEnd - _pad - chunkSamples, 0, _winOffset);
    }

    /// <summary>Forgets the stream: zero overlap, no carried sample.</summary>
    public void Reset()
    {
        Array.Clear(_overlap);
        _prev = 0f;
    }

    /// <summary>Floats of the carried state besides the last raw sample: the left overlap of the next chunk.</summary>
    public int OverlapLength => _overlap.Length;

    /// <summary>
    /// Copies what the next chunk depends on (the pre-emphasised overlap and the last raw sample) into <paramref name="overlap"/>,
    /// so a second branch of the stream can be decoded with its own front-end state and this one restored afterwards.
    /// </summary>
    /// <returns>The carried raw sample.</returns>
    public float SaveState(Span<float> overlap)
    {
        _overlap.CopyTo(overlap);
        return _prev;
    }

    /// <summary>Restores a state taken with <see cref="SaveState"/>.</summary>
    public void LoadState(ReadOnlySpan<float> overlap, float previous)
    {
        overlap[.._overlap.Length].CopyTo(_overlap);
        _prev = previous;
    }

    /// <summary>
    /// Computes the mel frames of the next chunk of the stream.
    /// </summary>
    /// <param name="chunk">Raw 16 kHz samples in [-1, 1].</param>
    /// <param name="lookahead">The raw samples that follow the chunk (clean mode), or empty for the faithful 56 zeros.</param>
    /// <param name="frames">Receives <c>FramesFor(chunk.Length) * NumMels</c> floats, time-major.</param>
    /// <returns>The number of frames written.</returns>
    public int Process(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookahead, Span<float> frames)
    {
        var n = chunk.Length;
        if (n == 0)
            throw new ArgumentException("A chunk needs at least one sample.", nameof(chunk));

        var frameCount = FramesFor(n);
        if (frameCount <= 0)
            throw new ArgumentException($"A chunk of {n} samples is shorter than one analysis window.", nameof(chunk));
        if (frames.Length < frameCount * _numMels)
            throw new ArgumentException("The destination is too small for the frames of this chunk.", nameof(frames));

        var paddedLength = _pad + n + _winOffset;
        if (_padded.Length < paddedLength)
            _padded = new float[paddedLength];
        var padded = _padded;

        // [ overlap | pre-emphasised chunk | tail ]
        _overlap.CopyTo(padded, 0);
        padded[_pad] = chunk[0] - _preemph * _prev;
        for (var i = 1; i < n; i++)
            padded[_pad + i] = chunk[i] - _preemph * chunk[i - 1];

        // The overlap for the next chunk is the last `_pad` samples of overlap ++ pre.
        Array.Copy(padded, n, _overlap, 0, _pad);

        var tail = padded.AsSpan(_pad + n, _winOffset);
        tail.Clear();
        var look = Math.Min(lookahead.Length, _winOffset);
        if (look > 0)
        {
            tail[0] = lookahead[0] - _preemph * chunk[n - 1];
            for (var i = 1; i < look; i++)
                tail[i] = lookahead[i] - _preemph * lookahead[i - 1];
        }
        _prev = chunk[n - 1];

        for (var t = 0; t < frameCount; t++)
            ComputeFrame(padded, t * _hop + _winOffset, frames.Slice(t * _numMels, _numMels));

        return frameCount;
    }

    private void ComputeFrame(float[] padded, int start, Span<float> mel)
    {
        var frame = _frame;
        for (var i = 0; i < _win; i++)
            frame[i] = padded[start + i] * _window[i];
        Array.Clear(frame, _win, _fftSize - _win);

        _fft.Power(frame, _power);

        for (var m = 0; m < _numMels; m++)
        {
            var sum = 0f;
            var count = _melBinCount[m];
            if (count > 0)
            {
                var first = _melFirstBin[m];
                var offset = _melWeightOffset[m];
                for (var j = 0; j < count; j++)
                    sum += _weights[offset + j] * _power[first + j];
            }
            mel[m] = MathF.Log(sum + _logEps);
        }
    }

    // ── Slaney mel filterbank, float arithmetic in the order of the C++ code ──

    private const float KMinLogHz = 1000.0f;
    private const float KMinLogMel = 15.0f;
    private const float KLinScale = 200.0f / 3.0f;
    private const float KLogStep = 0.06875177742094912f;

    private static float HzToMel(float hz) => hz < KMinLogHz ? hz / KLinScale : KMinLogMel + MathF.Log(hz / KMinLogHz) / KLogStep;

    private static float MelToHz(float mel) => mel < KMinLogMel ? mel * KLinScale : KMinLogHz * MathF.Exp((mel - KMinLogMel) * KLogStep);

    /// <summary>
    /// Slaney-scale, area-normalised triangular filters (librosa <c>htk=False, norm='slaney'</c>, 0 Hz to Nyquist),
    /// row-major <c>[numMels, fftSize / 2 + 1]</c>.
    /// </summary>
    internal static float[] CreateMelFilterbank(int numMels, int fftSize, int sampleRate)
    {
        var bins = fftSize / 2 + 1;
        var low = HzToMel(0f);
        var high = HzToMel(sampleRate / 2.0f);

        var melHz = new float[numMels + 2];
        for (var i = 0; i < numMels + 2; i++)
            melHz[i] = MelToHz(low + (high - low) * i / (numMels + 1));

        var diff = new float[numMels + 1];
        for (var i = 0; i < numMels + 1; i++)
            diff[i] = melHz[i + 1] - melHz[i];

        var freqs = new float[bins];
        for (var k = 0; k < bins; k++)
            freqs[k] = (float)k * sampleRate / fftSize;

        var fb = new float[numMels * bins];
        for (var m = 0; m < numMels; m++)
        {
            for (var k = 0; k < bins; k++)
            {
                var lower = (freqs[k] - melHz[m]) / (diff[m] + 1e-10f);
                var upper = (melHz[m + 2] - freqs[k]) / (diff[m + 1] + 1e-10f);
                fb[m * bins + k] = MathF.Max(0f, MathF.Min(lower, upper));
            }

            var enorm = 2.0f / (melHz[m + 2] - melHz[m] + 1e-10f);
            for (var k = 0; k < bins; k++)
                fb[m * bins + k] *= enorm;
        }
        return fb;
    }

    /// <summary>The symmetric Hann window of the analysis (a copy, for tests).</summary>
    internal float[] WindowCopy() => (float[])_window.Clone();
}
