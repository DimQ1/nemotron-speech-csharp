using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SpeechLib.Qwen3;

/// <summary>
/// Whisper-compatible log-mel spectrogram used as the Qwen3-ASR encoder input.
/// Parameters match preprocessor_config.json (WhisperFeatureExtractor):
/// 16 kHz, n_fft=400, hop=160, 128 mels, Slaney mel scale, 0-8 kHz.
/// Ported from the exporter reference (qwen3-asr-onnx/src/mel.py).
/// </summary>
internal static class WhisperMel
{
    public const int SampleRate = 16000;
    public const int NFFT = 400;
    public const int Hop = 160;
    public const int NMels = 128;

    private const int NBins = NFFT / 2 + 1; // 201

    private static readonly float[] Window = CreateHannWindow();
    private static readonly float[,] Filters = CreateMelFilterbank();

    /// <summary>
    /// Computes the log-mel spectrogram of a 16 kHz mono waveform.
    /// Returns values laid out [NMels, frames] (row per mel bin), matching the
    /// encoder input shape [1, 128, frames]. The last STFT frame is dropped,
    /// mirroring WhisperFeatureExtractor.
    /// </summary>
    public static float[] ComputeLogMel(float[] audio, out int frames)
    {
        frames = Math.Max(1, audio.Length / Hop);

        // STFT with center padding (reflect), periodic Hann window.
        var power = new float[NBins * frames];
        var buf = new Complex32[NFFT];
        for (int f = 0; f < frames; f++)
        {
            int start = f * Hop - NFFT / 2;
            for (int i = 0; i < NFFT; i++)
                buf[i] = new Complex32(ReflectAt(audio, start + i) * Window[i], 0f);

            Fourier.Forward(buf, FourierOptions.NoScaling);

            for (int b = 0; b < NBins; b++)
            {
                var c = buf[b];
                power[f * NBins + b] = c.Real * c.Real + c.Imaginary * c.Imaginary;
            }
        }

        // Mel projection: filters [NMels, NBins] x power [NBins, frames].
        var mel = new float[NMels * frames];
        for (int m = 0; m < NMels; m++)
        {
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                int rowBase = f * NBins;
                for (int b = 0; b < NBins; b++)
                    sum += Filters[m, b] * power[rowBase + b];
                mel[m * frames + f] = (float)sum;
            }
        }

        // log10 with clamp, dynamic-range compression (max - 8 dB), rescale.
        double max = double.NegativeInfinity;
        for (int i = 0; i < mel.Length; i++)
        {
            double v = Math.Log10(Math.Max(mel[i], 1e-10f));
            mel[i] = (float)v;
            if (v > max) max = v;
        }
        double floor = max - 8.0;
        for (int i = 0; i < mel.Length; i++)
            mel[i] = (float)((Math.Max(mel[i], floor) + 4.0) / 4.0);

        return mel;
    }

    private static float ReflectAt(float[] x, int i)
    {
        int n = x.Length;
        if (n == 0) return 0f;
        while (i < 0 || i >= n)
            i = i < 0 ? -i : 2 * n - 2 - i;
        return x[i];
    }

    /// <summary>Periodic Hann window, matching torch.hann_window(periodic=True).</summary>
    private static float[] CreateHannWindow()
    {
        var w = new float[NFFT];
        for (int i = 0; i < NFFT; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / NFFT));
        return w;
    }

    /// <summary>
    /// Slaney-normalized mel filterbank, matching
    /// librosa.filters.mel(sr=16000, n_fft=400, n_mels=128, fmin=0, fmax=8000, norm=slaney).
    /// </summary>
    private static float[,] CreateMelFilterbank()
    {
        var freqs = new double[NMels + 2];
        double lo = HzToMel(0.0), hi = HzToMel(8000.0);
        for (int m = 0; m < NMels + 2; m++)
            freqs[m] = MelToHz(lo + (hi - lo) * m / (NMels + 1));

        var w = new float[NMels, NBins];
        for (int m = 0; m < NMels; m++)
        {
            double f0 = freqs[m], f1 = freqs[m + 1], f2 = freqs[m + 2];
            double enorm = 2.0 / (f2 - f0);
            for (int k = 0; k < NBins; k++)
            {
                double fk = k * (double)SampleRate / NFFT;
                double lower = (fk - f0) / (f1 - f0);
                double upper = (f2 - fk) / (f2 - f1);
                w[m, k] = (float)(Math.Max(0.0, Math.Min(lower, upper)) * enorm);
            }
        }
        return w;
    }

    /// <summary>Slaney mel scale: linear below 1 kHz, logarithmic above.</summary>
    private static double HzToMel(double f) =>
        f < 1000.0 ? 3.0 * f / 200.0 : 15.0 + 27.0 * Math.Log(f / 1000.0) / Math.Log(6.4);

    private static double MelToHz(double m) =>
        m < 15.0 ? 200.0 * m / 3.0 : 1000.0 * Math.Exp(Math.Log(6.4) * (m - 15.0) / 27.0);
}
